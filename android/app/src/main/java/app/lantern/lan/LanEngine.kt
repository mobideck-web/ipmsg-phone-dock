package app.lantern.lan

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import android.provider.DocumentsContract
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.Closeable
import java.net.*
import java.security.*
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import javax.crypto.Cipher
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

data class IncomingNotice(val text: String, val user: String, val fileOffer: Boolean)

class LanEngine(private val model: Model, private val notify: (String, IncomingNotice) -> Unit,
                private val listenPort: Int = Wire.PORT, private val peerPort: Int = Wire.PORT) : Closeable {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val keys = ConcurrentHashMap<String, IpCrypto.Companion.PeerKey>()
    private val pending = ConcurrentHashMap<String, CompletableDeferred<Unit>>()
    private val fileReplay = ReplayWindow()
    private data class Offer(val uri: Uri, val transfer: Transfer, val expires: Long)
    private val offers = ConcurrentHashMap<String, Offer>()
    private val jobs = ConcurrentHashMap<String, Job>()
    private val sockets = ConcurrentHashMap<String, Socket>()
    private val slots = Semaphore(4)
    private val incomingLimit = PacketRateLimit()
    private lateinit var udp: DatagramSocket
    private data class LanSocket(val route: LanRoute, val networkHandle: Long?, val socket: DatagramSocket)
    private val lanSockets = ConcurrentHashMap<String, LanSocket>()
    @Volatile private var routeSignature = ""
    private lateinit var tcp: ServerSocket
    private lateinit var crypto: IpCrypto
    private lateinit var identity: String
    @Volatile private var closed = false
    private fun next() = model.packetSequence.next()
    private fun packet(command: Long, body: String, n: Long = next()) = Wire.Packet(n, identity, "LanternAndroid", command, body)
    private fun send(ip: String, p: Wire.Packet) {
        val route = LanRoute.select(LanRouting.routes(), ip)
        val socket = if (ip == "127.0.0.1") udp else route?.let { lanSockets[it.address]?.socket }
            ?: throw NoRouteToHostException("This device is outside the current local network. Reconnect it to this Wi-Fi or hotspot and check its current IP address.")
        val b = p.bytes(); socket.send(DatagramPacket(b, b.size, InetAddress.getByName(ip), peerPort))
    }
    @Synchronized private fun updateLanSockets(): List<LanRoute> {
        if (closed) return emptyList()
        val routes = LanRouting.routes()
        val addresses = routes.map { it.address }.toSet()
        lanSockets.entries.toList().filter { it.key !in addresses }.forEach { (address, binding) ->
            if (lanSockets.remove(address, binding)) binding.socket.close()
        }
        val bindings = routes.associateWith { LanRouting.network(model.context, it.address) }
        bindings.forEach { (route, network) ->
            val old = lanSockets[route.address]
            if (old?.route == route && old.networkHandle == network?.networkHandle && !old.socket.isClosed) return@forEach
            old?.socket?.close(); lanSockets.remove(route.address)
            val socket = DatagramSocket(null)
            try {
                socket.reuseAddress = true; socket.broadcast = true
                LanRouting.bind(model.context, socket, route, listenPort, network)
                lanSockets[route.address] = LanSocket(route, network?.networkHandle, socket)
                scope.launch { receiveLoop(socket) }
            } catch (e: Exception) { socket.close(); android.util.Log.w("IPMsgLAN", "LAN socket unavailable for ${route.address}", e) }
        }
        val signature = routes.sortedBy { it.address }.joinToString { "${it.address}/${it.prefix}:${bindings[it]?.networkHandle}" }
        if (routeSignature.isNotEmpty() && signature != routeSignature) {
            keys.clear()
            model.update { it.copy(peers = emptyList(), revision = it.revision + 1) }
        }
        if (signature != routeSignature) model.changed()
        routeSignature = signature
        return routes
    }
    fun start() = scope.launch {
        try {
            val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            val alias = "lantern-ipmsg"
            if (!store.containsAlias(alias)) {
                KeyPairGenerator.getInstance("RSA", "AndroidKeyStore").apply {
                    initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT or KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                        .setKeySize(2048).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_RSA_PKCS1)
                        .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1).setDigests(KeyProperties.DIGEST_SHA1, KeyProperties.DIGEST_SHA256).build())
                }.generateKeyPair()
            }
            crypto = IpCrypto(KeyPair(store.getCertificate(alias).publicKey, store.getKey(alias, null) as PrivateKey))
            identity = "${model.user}-<${IpCrypto.userDigest(crypto.pair.public)}>"
            udp = DatagramSocket(null).apply { reuseAddress = true; broadcast = true; bind(InetSocketAddress(listenPort)) }
            tcp = ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(listenPort)) }
            if (closed) { close(); return@launch }
            model.update { it.copy(listening = true, error = null) }
            launch { receiveLoop(udp) }; launch { acceptLoop() }
            refresh()
            launch {
                while (isActive) {
                    delay(60_000); refresh()
                    model.update { s -> s.copy(peers = s.peers.filter { System.currentTimeMillis() - it.seen < 180_000 }) }
                    expireOffers()
                }
            }
        } catch (e: Exception) { model.error(e); close() }
    }
    fun refresh(ip: String? = null) = scope.launch {
        if (!model.state.value.listening || closed) return@launch
        val routes = updateLanSockets()
        val destinations = if (ip != null) listOf(ip) else buildList {
            addAll(routes.mapNotNull { it.broadcast })
            addAll(model.preferences.getStringSet("manual", emptySet()).orEmpty())
        }.distinct()
        val sent = discoverIndependently(destinations) { send(it, packet(1L or Wire.CAPS, model.name + "\u0000LAN")) }
        if (sent == 0) model.error(IllegalStateException(if (routes.isEmpty())
            "No local network. Enable Wi-Fi or a hotspot; mobile data is not needed."
        else "Cannot reach this local network. Reconnect Windows to the same Wi-Fi or hotspot and check its current IP address."))
    }
    fun addIp(ip: String) = scope.launch {
        runCatching {
            require(ip.matches(Regex("(?:[0-9]{1,3}\\.){3}[0-9]{1,3}")) && ip.split('.').all { it.toInt() in 0..255 }) { "Enter an IPv4 address, for example 192.168.1.20" }
            if (model.blocked(ip)) model.restore(ip)
            val saved = (model.preferences.getStringSet("manual", emptySet()) ?: emptySet()).toMutableSet(); saved.add(ip)
            model.preferences.edit().putStringSet("manual", saved).apply(); model.update { it.copy(revision = it.revision + 1) }; refresh(ip)
        }.onFailure(model::error)
    }
    fun revokeDeviceOffers(ip: String) { model.state.value.transfers.filter { it.ip == ip && (it.active || it.phase == TransferPhase.OFFERED || it.phase == TransferPhase.FAILED) }.forEach { cancel(it.id) } }
    fun resetTrust(ip: String, user: String) {
        model.repository.devices().filter { it.ip == ip && it.user == user }.forEach { model.devices.revoke(it) }
        model.preferences.edit().remove("pin-$ip-$user").apply(); keys.remove(ip); refresh(ip)
    }
    fun sendBatch(ip: String, text: String, uris: List<Uri>, allowPlain: Boolean, expectedUser: String, onQueued: (Boolean) -> Unit = {}) = scope.launch {
        val batch = java.util.UUID.randomUUID().toString()
        batchJobs[batch] = currentCoroutineContext().job
        var success = true
        var child: Job? = null
        try {
            require(uris.size <= 30) { "Select at most 30 files" }
            if (uris.isEmpty()) { child=sendMessage(ip,text,allowPlain=allowPlain,expectedUser=expectedUser,onQueued={ success = it });child.join() }
            else for ((index,uri) in uris.withIndex()) {
                currentCoroutineContext().ensureActive()
                child=sendMessage(ip,if(index == 0) text else "",uri,allowPlain,expectedUser,batchId=batch,onQueued={ success = success && it });child.join()
                if (!success) break
            }
            onQueued(success)
        } catch(e: Exception) { onQueued(false); if(e !is CancellationException) model.error(e) }
        finally { if(!currentCoroutineContext().isActive) child?.cancel(); batchJobs.remove(batch) }
    }
    fun cancelBatch(id: String) { batchJobs[id]?.cancel(); model.state.value.transfers.filter { it.batchId == id }.forEach { cancel(it.id) } }
    private val batchJobs = ConcurrentHashMap<String, Job>()
    fun encrypted(ip: String) = IpCrypto.compatible(keys[ip])
    fun fingerprint(ip: String): String? = keys[ip]?.let { IpCrypto.hex(MessageDigest.getInstance("SHA-256").digest(it.key.encoded)).chunked(4).joinToString(" ") }
    private suspend fun receiveLoop(socket: DatagramSocket) {
        val buf = ByteArray(32769)
        while (scope.isActive && !closed && !socket.isClosed) {
            try {
                val datagram = DatagramPacket(buf, buf.size); socket.receive(datagram)
                val p = Wire.parse(buf.copyOf(datagram.length)); val ip = datagram.address.hostAddress ?: continue
                if (p.user == identity) continue
                handle(ip, p)
            } catch (e: Exception) { if (e is SocketException && (closed || socket.isClosed)) break /* Ignore malformed or unauthenticated LAN packets. */ }
        }
    }
    private suspend fun handle(ip: String, p: Wire.Packet) {
        if (model.blocked(ip) || model.devices.record(ip,p.user)?.blocked == true || !incomingLimit.allow(ip)) return
        when (p.mode) {
            1, 3, 4 -> {
                val previous = model.state.value.peers.find { it.ip == ip }
                if (previous == null && model.state.value.peers.size >= 128) return
                if (previous != null && (previous.user != p.user || previous.host != p.host)) keys.remove(ip)
                val peer = Peer(ip, p.body.substringBefore('\u0000').ifBlank { p.user }, p.user, p.host, p.command)
                model.update { it.copy(peers = (it.peers.filterNot { old -> old.ip == ip } + peer).sortedBy { item -> item.name }) }
                model.devices.observed(peer)
                if (previous == null && model.setting("device-alerts", true)) Events.popup(model.context, peer.name, "Connected", ip, peer.user)
                if (p.mode == 1) send(ip, packet(3L or Wire.CAPS, model.name + "\u0000LAN"))
                if (p.command and Wire.ENCRYPT != 0L && !keys.containsKey(ip)) send(ip, packet(0x72L or Wire.UTF8, IpCrypto.CAPS.toString(16)))
            }
            2 -> if (NetworkSafety.sameIdentity(model.state.value.peers.find { it.ip == ip }, p.user, p.host)) {
                keys.remove(ip); model.update { it.copy(peers = it.peers.filterNot { old -> old.ip == ip }) }
            }
            0x72 -> send(ip, packet(0x73L or Wire.UTF8, crypto.publicBody()))
            0x73 -> {
                val announced = model.state.value.peers.find { it.ip == ip }
                if (announced == null) { refresh(ip); return }
                if (announced.user != p.user || announced.host != p.host) return
                val key = IpCrypto.parseKey(p.body)
                val advertisedDigest = Regex("-<([0-9a-fA-F]{16})>$").find(p.user)?.groupValues?.get(1)
                require(advertisedDigest == null || advertisedDigest.equals(IpCrypto.userDigest(key.key), true)) { "Public key does not match the peer identity" }
                val digest = IpCrypto.hex(MessageDigest.getInstance("SHA-256").digest(key.key.encoded))
                val old = model.preferences.getString("pin-$ip-${p.user}", null)
                if (old != null && old != digest) {
                    keys.remove(ip); model.error(IllegalStateException("Key changed for $ip. Verify the Windows key, then review this device in Nearby > Manage.")); return
                }
                model.preferences.edit().putString("pin-$ip-${p.user}", digest).apply()
                keys[ip] = key
                model.devices.observed(announced)
                model.update { it.copy(revision = it.revision + 1) }
            }
            0x21 -> if (NetworkSafety.sameIdentity(model.state.value.peers.find { it.ip == ip }, p.user, p.host)) pending["$ip:${p.body.substringBefore('\u0000')}"]?.complete(Unit)
            0x40 -> send(ip, packet(0x41L or Wire.UTF8, "IPMsg ${model.context.packageManager.getPackageInfo(model.context.packageName, 0).versionName} Android"))
            0x30 -> if (p.command and 0x100000 != 0L) send(ip, packet(0x32L or Wire.UTF8, p.body.substringBefore('\u0000')))
            0x61 -> if (NetworkSafety.sameIdentity(model.state.value.peers.find { it.ip == ip }, p.user, p.host)) offers.entries.filter { it.value.transfer.ip == ip && it.value.transfer.packet.toString(16) == p.body.substringBefore(':').substringBefore('\u0000') }.forEach {
                cancel(it.value.transfer.id); offers.remove(it.key)
            }
            0x20 -> {
                val encrypted = p.command and Wire.ENCRYPT != 0L
                if (!encrypted && model.setting("strict-messages")) return
                var security = "Plaintext"
                val content = if (encrypted) {
                    if (!NetworkSafety.sameIdentity(model.state.value.peers.find { it.ip == ip }, p.user, p.host)) { refresh(ip); return }
                    if (keys[ip] == null) {
                        send(ip, packet(0x72L or Wire.UTF8, IpCrypto.CAPS.toString(16)))
                        return // Withhold ACK; sender retries once its key has been exchanged.
                    }
                    val result = crypto.decrypt(p.body, p.number, keys[ip])
                    require(result.signed || keys.getValue(ip).capabilities and (IpCrypto.SHA1 or IpCrypto.SHA256) == 0L) { "Unsigned message from a signing-capable peer" }
                    security = if (result.signed) "Encrypted · signature verified" else "Encrypted · unsigned"
                    val charset = if (p.command and Wire.UTF8 != 0L) Charsets.UTF_8 else java.nio.charset.Charset.forName("windows-31j")
                    val decoded = result.bytes.toString(charset)
                    if (p.command and Wire.EXT != 0L) decoded else decoded.substringBefore('\u0000') + "\u0000" + p.body.substringAfter('\u0000', "")
                } else p.body
                val text = content.substringBefore('\u0000')
                if (!encrypted && p.user.contains("-<")) security = "Plaintext · identity unverified"
                val saveContent = p.command and 0x20000 == 0L
                val firstReceipt = model.history.receive(p.number, ip, p.user, p.host, text, security, saveContent)
                if (p.command and 0x100 != 0L && p.command and (0x400 or 0x2000).toLong() == 0L) send(ip, packet(0x21L or Wire.UTF8, p.number.toString()))
                if (!firstReceipt) return
                if (model.state.value.peers.none { it.ip == ip }) {
                    model.update { it.copy(peers = it.peers + Peer(ip, p.user, p.user, p.host, p.command)) }; refresh(ip)
                }
                if (saveContent) model.reload()
                else model.ephemeral(Chat(-next(), ip, text, false, security, "Not saved", peerUser = p.user, seen = false, packet = p.number))
                if (p.command and Wire.FILE != 0L) {
                    Wire.attachments(content.substringAfter('\u0000', "").substringBefore('\u0000')).take(30).forEach { file ->
                        model.transfer(Transfer("in:$ip:${p.number}:${file.id}", ip, p.number, file, false, peerUser = p.user, remember = saveContent))
                    }
                }
                notify(ip, IncomingNotice(if (p.command and Wire.FILE != 0L) "File offer received" else text.take(160), p.user, p.command and Wire.FILE != 0L))
            }
        }
    }
    fun sendMessage(ip: String, text: String, uri: Uri? = null, allowPlain: Boolean = false, expectedUser: String? = null, batchId: String? = null, onQueued: (Boolean) -> Unit = {}) = scope.launch {
        var number: Long? = null
        try {
            require(model.state.value.listening) { "Enable listening first" }
            val peer = requireNotNull(model.state.value.peers.find { it.ip == ip }) { "This device is offline. Refresh Nearby before sending." }
            require(expectedUser == null || peer.user == expectedUser) { "Device identity changed at $ip. Select the intended device in Nearby." }
            require(text.toByteArray().size <= 6000 && '\u0000' !in text) { "Message limit is 6,000 UTF-8 bytes; NUL characters are not supported" }
            require(!model.blocked(ip)) { "Device removed. Restore it in Settings." }
            model.devices.authorize(ip,peer.user,encrypted(ip),allowPlain)
            require(encrypted(ip) || (allowPlain && !model.setting("strict-messages"))) { "Secure messaging unavailable. Check Security in Settings." }
            require(uri == null || !model.setting("strict-files") || (encrypted(ip) && peer.command and Wire.FILE_ENC != 0L)) {
                "This Windows device does not advertise encrypted file downloads. Update Windows IP Messenger or turn off 'Require encrypted files' in Settings > Security if you accept unencrypted file contents."
            }
            require(uri == null || offers.size < 100) { "Too many file offers; stop listening to clear them" }
            val n = next(); number = n
            var attachment: Wire.Attachment? = null
            if (uri != null) {
                model.context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                    require(c.moveToFirst() && !c.isNull(1)) { "This provider does not report file size; save the file locally first" }
                    attachment = Wire.Attachment(0, Wire.safeName(c.getString(0) ?: "file"), c.getLong(1).also { require(it >= 0) })
                }
                requireNotNull(attachment) { "Unable to read file metadata" }
            }
            val isEncrypted = encrypted(ip)
            require(peer.command and (Wire.CAP_UTF8 or Wire.UTF8) != 0L) { "This peer lacks UTF-8 support" }
            val plain = text.replace("\r\n", "\n") + "\u0000" + (attachment?.offer() ?: "") + if (attachment != null) "\u0000" else ""
            var command = 0x20L or 0x100L or Wire.UTF8
            if (attachment != null) command = command or Wire.FILE
            val body = if (isEncrypted) {
                command = command or Wire.ENCRYPT
                if (attachment != null && peer.command and Wire.EXT != 0L) {
                    command = command or Wire.EXT; crypto.encrypt(plain.toByteArray(), n, keys.getValue(ip))
                } else crypto.encrypt((text.replace("\r\n", "\n") + "\u0000").toByteArray(), n, keys.getValue(ip)) + if (attachment != null) "\u0000${attachment!!.offer()}" else ""
            } else plain
            val wire = packet(command, body, n); wire.bytes() // Validate before persisting or exposing a file.
            model.history.add(n, ip, text.ifBlank { "File: ${attachment?.name}" }, true, if (isEncrypted) "Encrypted" else "Plaintext", "Sending", peer.user); model.reload()
            if (uri != null && attachment != null) {
                val t = Transfer("out:$ip:$n:0", ip, n, attachment!!, true, peerUser = peer.user, sourceUri = uri.toString()).copy(batchId = batchId)
                model.transfer(t); offers["$ip:$n:0"] = Offer(uri, t, System.currentTimeMillis() + 3_600_000)
            }
            onQueued(true)
            val ack = CompletableDeferred<Unit>(); val key = "$ip:$n"; pending[key] = ack
            try {
                repeat(4) { attempt ->
                    send(ip, wire)
                    if (withTimeoutOrNull((attempt + 1) * 1500L) { ack.await(); true } == true) {
                        model.history.status(n, ip, "Delivered"); model.reload(); return@launch
                    }
                }
                model.history.status(n, ip, "No acknowledgment — delivery unknown"); model.reload()
            } finally { pending.remove(key) }
        } catch (e: Exception) {
            if (e is CancellationException) throw e
            onQueued(false)
            number?.let { model.history.status(it, ip, "Failed: ${e.message}"); model.reload() }
            model.error(e)
        }
    }
    private suspend fun acceptLoop() {
        while (!closed && scope.isActive) {
            try {
                val socket = tcp.accept().apply { soTimeout = 15_000 }
                if (sockets.size >= 8) { socket.close(); continue }
                val temporary = "request:${next()}"; sockets[temporary] = socket
                scope.launch {
                    try { slots.withPermit { serve(socket) } } catch (_: Exception) { } finally { sockets.remove(temporary); socket.close() }
                }
            } catch (e: SocketException) { if (closed) break }
        }
    }
    private suspend fun serve(socket: Socket) {
        val ip = socket.inetAddress.hostAddress ?: return
        require(!model.blocked(ip)) { "Device removed" }
        require(model.devices.approved(ip, model.state.value.peers.find { it.ip == ip }?.user.orEmpty())) { "Device approval revoked" }
        val p = Wire.parse(NetworkSafety.readRequest(socket)); require(p.mode == 0x60 || p.mode == 0x62)
        require(NetworkSafety.sameIdentity(model.state.value.peers.find { it.ip == ip }, p.user, p.host)) { "File requester identity changed" }
        val framed = p.mode == 0x62
        val request = if (p.command and Wire.ENCRYPT != 0L) {
            val d = crypto.decrypt(p.body, p.number, keys[ip]); require(d.signed) { "Signed file requests required" }
            d.bytes.toString(Charsets.UTF_8).substringBefore('\u0000')
        } else p.body.substringBefore('\u0000')
        val a = request.split(':'); require(a.size >= if (framed) 2 else 3)
        val n = a[0].toLong(16); val file = a[1].toLong(16); val offset = if (framed) 0 else a[2].toLong(16)
        val base = if (framed) 2 else 3
        val fileCap = if (p.command and 0x800 != 0L) a[base].toLong(16) else 0L
        require(fileCap == 0L || fileCap == 0x900000L || fileCap == 0x4000000L)
        val encrypted = fileCap == 0x900000L
        val offer = offers["$ip:$n:$file"] ?: return
        require(System.currentTimeMillis() <= offer.expires && offset in 0..offer.transfer.file.size)
        if (model.setting("strict-files") && !encrypted) {
            if (!jobs.containsKey(offer.transfer.id)) model.transfer(offer.transfer.copy(phase = TransferPhase.FAILED, failure = FailureReason.SECURITY,
                detail = "Windows requested this file without encryption. Update Windows IP Messenger, or review Require encrypted files in Settings > Security, then retry."))
            return // Keep the original offer/expiry; never silently downgrade encryption.
        }
        if (fileCap != 0L) {
            require(p.command and Wire.ENCRYPT != 0L)
            val nonce = a.getOrNull(base + if (encrypted) 2 else 1)?.takeIf { it.isNotBlank() }?.toLong(16)
            require(nonce == null || nonce == p.number)
            require(fileReplay.first("$ip:${p.user}:${p.number}")) { "Replayed file request" }
        }
        val id = offer.transfer.id
        require(jobs.putIfAbsent(id, currentCoroutineContext().job) == null) { "Transfer already active" }
        sockets[id] = socket
        var t = offer.transfer.copy(phase = TransferPhase.SENDING, done = offset, encrypted = encrypted)
        val lease = TransferLease(model.context); val rate = TransferRate()
        val lastActivity = AtomicLong(System.nanoTime())
        val watchdog = scope.launch {
            while (isActive) { delay(5000); if (System.nanoTime() - lastActivity.get() > 30_000_000_000L) { socket.close(); break } }
        }
        try {
            val cipher = if (encrypted) {
                val key = IpCrypto.unhex(a[base + 1]); require(key.size == 32)
                Cipher.getInstance("AES/CTR/NoPadding").apply { init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), IvParameterSpec(IpCrypto.iv(p.number, true))) }
            } else null
            model.context.contentResolver.openInputStream(offer.uri)!!.use { fileInput ->
                var skip = offset
                while (skip > 0) { val s = fileInput.skip(skip); require(s > 0); skip -= s }
                val output = socket.getOutputStream(); val buffer = ByteArray(131072)
                val encoded = ByteArray(buffer.size + 32)
                if (framed) {
                    val header = Wire.fileHeader(t.file)
                    output.write(cipher?.update(header) ?: header)
                }
                var last = 0L
                while (t.done < t.file.size) {
                    currentCoroutineContext().ensureActive()
                    val count = fileInput.read(buffer, 0, minOf(buffer.size.toLong(), t.file.size - t.done).toInt())
                    if(count<=0)throw SourceChangedException()
                    if (cipher == null) output.write(buffer, 0, count)
                    else { val encodedCount = cipher.update(buffer, 0, count, encoded, 0); output.write(encoded, 0, encodedCount) }
                    lastActivity.set(System.nanoTime())
                    t = t.copy(done = t.done + count).let { it.copy(speed = rate.speed(it.done).takeIf { n -> n > 0 } ?: it.speed) }
                    if (System.currentTimeMillis() - last > 200) { model.transfer(t); last = System.currentTimeMillis() }
                }
                cipher?.doFinal()?.let { if (it.isNotEmpty()) output.write(it) }; output.flush()
            }
            model.transfer(t.copy(phase = TransferPhase.SENT))
            offers.remove("$ip:$n:$file")
        } catch (e: Exception) { model.transfer(t.copy(phase = if (!currentCoroutineContext().isActive) TransferPhase.CANCELLED else TransferPhase.FAILED, failure = FailureReason.of(e), detail = e.message)) }
        finally { lease.close(); watchdog.cancel(); sockets.remove(id); jobs.remove(id); if (System.currentTimeMillis() > offer.expires || !currentCoroutineContext().isActive || model.state.value.transfers.find {it.id==id}?.failure in setOf(FailureReason.SOURCE_CHANGED,FailureReason.PERMISSION,FailureReason.STORAGE,FailureReason.IDENTITY)) offers.remove("$ip:$n:$file") }
    }
    fun retainedFileUris(): Set<String> = offers.values.map { it.uri.toString() }.toSet()
    fun retryAvailable(t: Transfer): Boolean = offers.values.any { it.transfer.id == t.id && it.expires > System.currentTimeMillis() } && !jobs.containsKey(t.id)
    fun download(t: Transfer, destination: Uri, allowPlain: Boolean) {
        if (jobs.containsKey(t.id)) { deletePartial(destination); return }
        require(!t.outgoing && t.phase in setOf(TransferPhase.OFFERED,TransferPhase.FAILED,TransferPhase.CANCELLED,TransferPhase.INTERRUPTED)) { "Request a fresh file offer from the sender" }
        model.transfer(t.copy(phase = TransferPhase.QUEUED))
        val job = scope.launch(start = CoroutineStart.LAZY) {
            var transfer = t.copy(done = 0, savedUri = null)
            var lease: TransferLease? = null
            val rate = TransferRate()
            try {
                slots.withPermit {
                    lease = TransferLease(model.context)
                    val peer = model.state.value.peers.find { it.ip == t.ip }
                    require(t.peerUser.isEmpty() || peer?.user == t.peerUser) { "File sender identity changed. Request a new offer." }
                    val secure = encrypted(t.ip) && peer != null && peer.command and Wire.FILE_ENC != 0L
                    require(secure || allowPlain) { "Plaintext file consent required" }
                    val n = next(); val secret = ByteArray(32).also { SecureRandom().nextBytes(it) }
                    var body = "${t.packet.toString(16)}:${t.file.id.toString(16)}:0"
                    var command = 0x60L or Wire.UTF8
                    if (secure) { body += ":900000:${IpCrypto.hex(secret)}:${n.toString(16)}"; body = crypto.encrypt((body + "\u0000").toByteArray(), n, keys.getValue(t.ip)); command = command or Wire.ENCRYPT or 0x800L }
                    transfer = transfer.copy(phase = TransferPhase.RECEIVING, encrypted = secure); model.transfer(transfer)
                    require(!model.blocked(t.ip)) { "Device removed" }
                    val socket = Socket().apply { receiveBufferSize = 262144 }; sockets[t.id] = socket
                    socket.use {
                        LanRouting.bind(model.context, socket, t.ip)
                        socket.connect(InetSocketAddress(t.ip, peerPort), 10_000); socket.soTimeout = 20_000
                        socket.getOutputStream().write(packet(command, body, n).bytes())
                        val cipher = if (secure) Cipher.getInstance("AES/CTR/NoPadding").apply { init(Cipher.DECRYPT_MODE, SecretKeySpec(secret, "AES"), IvParameterSpec(IpCrypto.iv(n, true))) } else null
                        model.context.contentResolver.openOutputStream(destination, "wt")!!.use { output ->
                            var last = 0L
                            val coroutine = currentCoroutineContext()
                            val checkedOutput=object:java.io.FilterOutputStream(output){
                                override fun write(bytes:ByteArray,offset:Int,length:Int){try{out.write(bytes,offset,length)}catch(e:java.io.IOException){throw StorageWriteException(e)}}
                                override fun flush(){try{out.flush()}catch(e:java.io.IOException){throw StorageWriteException(e)}}
                            }
                            FileTransfer.receive(socket.getInputStream(), checkedOutput, t.file.size, cipher,
                                checkActive = { coroutine.ensureActive() }, progress = { received ->
                                    transfer = transfer.copy(done = received, speed = rate.speed(received).takeIf { it > 0 } ?: transfer.speed)
                                    if (System.currentTimeMillis() - last > 200) { model.transfer(transfer); last = System.currentTimeMillis() }
                                })
                        }
                    }
                    DownloadStorage.publish(model, destination, t.file.size)
                    model.transfer(transfer.copy(phase = TransferPhase.SAVED, savedUri = destination.toString()))
                    Events.popup(model.context, transfer.file.name, "Saved", t.ip, t.peerUser)
                }
            } catch (e: Exception) {
                deletePartial(destination)
                model.transfer(transfer.copy(phase = if (!currentCoroutineContext().isActive) TransferPhase.CANCELLED else TransferPhase.FAILED, failure = FailureReason.of(e), detail = e.message))
            } finally { lease?.close(); sockets.remove(t.id)?.close(); jobs.remove(t.id) }
        }
        if (jobs.putIfAbsent(t.id, job) == null) job.start() else { job.cancel(); deletePartial(destination) }
    }
    fun cancel(id: String) {
        jobs[id]?.cancel(); runCatching { sockets[id]?.close() }
        offers.entries.removeIf { it.value.transfer.id == id }
        model.state.value.transfers.find { it.id == id }?.let { model.transfer(it.copy(phase = TransferPhase.CANCELLED)) }
    }
    fun removeDevice(ip: String) {
        val peer=model.state.value.peers.find {it.ip==ip}
        val known=peer?.let {model.devices.record(ip,it.user)}
        if(known?.fingerprint != null) model.devices.revoke(known,true)
        model.preferences.edit().putStringSet("approved-devices", model.preferences.getStringSet("approved-devices", emptySet()).orEmpty().filterNot { it.startsWith("$ip|") }.toSet()).apply()
        model.preferences.edit().putStringSet("blocked", if(known?.fingerprint != null) model.preferences.getStringSet("blocked", emptySet()).orEmpty() - ip else model.preferences.getStringSet("blocked", emptySet()).orEmpty() + ip)
            .putStringSet("manual", model.preferences.getStringSet("manual", emptySet()).orEmpty() - ip).apply()
        model.state.value.transfers.filter { it.ip == ip && (it.active || it.phase in setOf(TransferPhase.OFFERED,TransferPhase.FAILED)) }.forEach { cancel(it.id) }
        keys.remove(ip)
        model.update { it.copy(peers = it.peers.filterNot { peer -> peer.ip == ip }, revision = it.revision + 1) }
    }
    suspend fun revokeConversation(ip: String? = null, user: String? = null) {
        val active = model.state.value.transfers.filter { (ip == null || it.ip == ip) && (user == null || it.peerUser == user) && (it.active || it.phase in setOf(TransferPhase.OFFERED,TransferPhase.FAILED)) }
        val running = active.mapNotNull { jobs[it.id] }
        active.forEach { cancel(it.id) }
        running.joinAll()
    }
    fun resetTrust() {
        model.preferences.edit().also { e -> model.preferences.all.keys.filter { it.startsWith("pin-") }.forEach { e.remove(it) } }.apply()
        keys.clear(); refresh()
    }
    internal fun expireOffers(now: Long = System.currentTimeMillis()) {
        offers.entries.toList().filter { now > it.value.expires && !jobs.containsKey(it.value.transfer.id) }.forEach { entry ->
            if (offers.remove(entry.key, entry.value)) model.transfer(entry.value.transfer.copy(phase = TransferPhase.EXPIRED))
        }
    }
    private fun deletePartial(uri: Uri) { DownloadStorage.cleanup(model,listOf(uri)) }
    override fun close() {
        closed = true
        if (::udp.isInitialized && !udp.isClosed) {
            discoverIndependently(LanRouting.routes().mapNotNull { it.broadcast }) { send(it, packet(2L or Wire.UTF8, model.name)) }
            udp.close()
        }
        synchronized(this) { lanSockets.values.forEach { it.socket.close() }; lanSockets.clear() }
        if (::tcp.isInitialized) runCatching { tcp.close() }
        sockets.values.forEach { runCatching { it.close() } }; sockets.clear()
        offers.clear(); batchJobs.values.forEach { it.cancel() }; batchJobs.clear(); scope.cancel()
        model.history.interruptPending(); model.reload()
        model.update { s -> s.copy(listening = false, peers = emptyList(), transfers = s.transfers.map {
            if (it.active || it.phase == TransferPhase.OFFERED) it.copy(phase = TransferPhase.INTERRUPTED) else it
        }) }
    }
}
