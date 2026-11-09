package app.lantern.lan

import android.net.Uri
import android.content.ContentValues
import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.net.DatagramSocket
import java.net.DatagramPacket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.io.File
import kotlin.concurrent.thread
import javax.crypto.Cipher
import javax.crypto.spec.SecretKeySpec
import javax.crypto.spec.IvParameterSpec

@RunWith(AndroidJUnit4::class)
class EngineTest {
    private fun await(condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + 30_000
        while (!condition()) { check(System.currentTimeMillis() < deadline) { "Timed out waiting for engine state" }; Thread.sleep(100) }
    }
    @Test fun liveSocketsKeystoreSignaturesDedupAckRetryAndFiles() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val model = Model.get(context)
        model.history.clear(); model.reload()
        val peer = DatagramSocket(0, InetAddress.getByName("127.0.0.1")).apply { soTimeout = 20_000 }
        val port = ServerSocket(0).use { it.localPort }
        val engine = LanEngine(model, { _, _ -> }, port, peer.localPort)
        val identity = "test-${System.nanoTime()}"
        val mock = IpCrypto.generate()
        fun transmit(n: Long, cmd: Long, body: String, sender: String = identity) {
            val raw = Wire.Packet(n, sender, "test", cmd, body).bytes()
            peer.send(DatagramPacket(raw, raw.size, InetAddress.getByName("127.0.0.1"), port))
        }
        fun receive(mode: Int): Wire.Packet {
            val deadline = System.currentTimeMillis() + 20_000
            while (System.currentTimeMillis() < deadline) {
                val d = DatagramPacket(ByteArray(32768), 32768); peer.receive(d)
                val p = Wire.parse(d.data.copyOf(d.length))
                if (p.mode == mode) return p
            }
            error("Expected command $mode")
        }
        model.engine=engine
        engine.start()
        try {
            await { model.state.value.listening }
            transmit(10, 1L or Wire.CAPS, "Test desktop")
            receive(3)
            transmit(11, 0x73L or Wire.UTF8, mock.publicBody())
            transmit(12, 0x72L or Wire.UTF8, IpCrypto.CAPS.toString(16))
            val appKey = IpCrypto.parseKey(receive(0x73).body)
            await { engine.encrypted("127.0.0.1") }
            model.devices.approve(model.state.value.peers.first {it.ip=="127.0.0.1"})
            val encrypted = mock.encrypt("مرحبا 🌍\u0000".toByteArray(), 20, appKey)
            repeat(2) { transmit(20, 0x20L or 0x100L or Wire.ENCRYPT or Wire.UTF8, encrypted); assertEquals("20", receive(0x21).body) }
            await { model.state.value.chats.size == 1 }
            assertEquals("مرحبا 🌍", model.state.value.chats.single().text)
            assertTrue(model.state.value.chats.single().security.contains("verified"))
            transmit(25, 0x20L or 0x100L or Wire.ENCRYPT or Wire.UTF8, mock.encrypt("forged identity\u0000".toByteArray(), 25, appKey), "impostor")
            transmit(26, 2L or Wire.UTF8, "bye", "impostor")
            transmit(29, 0x20L or 0x100L or Wire.ENCRYPT or Wire.UTF8, mock.encrypt("unsigned downgrade\u0000".toByteArray(), 29, appKey.copy(capabilities = IpCrypto.RSA or IpCrypto.AES)))
            Thread.sleep(300)
            assertEquals(1, model.state.value.chats.size)
            assertTrue(model.state.value.peers.any { it.user == identity })
            transmit(27, 0x20L or 0x100L or 0x20000L or Wire.ENCRYPT or Wire.UTF8, mock.encrypt("do not log\u0000".toByteArray(), 27, appKey))
            receive(0x21)
            await { model.state.value.chats.any { it.text == "do not log" && it.status == "Not saved" } }
            assertFalse(model.history.load().any { it.text == "do not log" })
            engine.sendMessage("127.0.0.1", "misdirected", expectedUser = "different-device")
            await { model.state.value.error?.contains("identity changed") == true }
            assertFalse(model.history.load().any { it.text == "misdirected" })
            engine.sendMessage("127.0.0.1", "Android reply")
            val outgoing = receive(0x20)
            transmit(28, 0x21L or Wire.UTF8, outgoing.number.toString(), "impostor")
            // Deliberately drop the first acknowledgment; the same packet must be retried.
            val retried = receive(0x20)
            assertEquals(outgoing.number, retried.number)
            assertEquals("Android reply\u0000", mock.decrypt(outgoing.body, outgoing.number, appKey).bytes.toString(Charsets.UTF_8))
            transmit(21, 0x21L or Wire.UTF8, outgoing.number.toString())
            await { model.state.value.chats.any { it.outgoing && it.status == "Delivered" } }

            // Upload exact bytes, including an empty file, through real TCP sockets.
            for ((length, secure, framed) in listOf(Triple(0, false, false), Triple(262144, false, false), Triple(0, true, false), Triple(262144, true, false), Triple(0, true, true), Triple(262144, true, true))) {
                val source = File(context.cacheDir, "upload-$length.bin").apply { writeBytes(ByteArray(length) { (it % 251).toByte() }) }
                val sourceUri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, source.name); put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream"); put(MediaStore.Downloads.IS_PENDING, 1)
                })!!
                context.contentResolver.openOutputStream(sourceUri)!!.use { it.write(source.readBytes()) }
                context.contentResolver.update(sourceUri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
                engine.sendMessage("127.0.0.1", "File test", sourceUri)
                val offer = receive(0x20)
                transmit(22, 0x21L or Wire.UTF8, offer.number.toString())
                val t = model.state.value.transfers.last { it.outgoing }
                Socket("127.0.0.1", port).use { s ->
                    s.soTimeout = 10_000
                    val secret = ByteArray(32) { it.toByte() }; val reqNo = 300 + length.toLong() + if (framed) 2 else 0
                    var body = "${offer.number.toString(16)}:0" + if (framed) "" else ":0"
                    var cmd = (if (framed) 0x62L else 0x60L) or Wire.UTF8
                    if (secure) { body += ":900000:${IpCrypto.hex(secret)}:${reqNo.toString(16)}"; body = mock.encrypt((body + "\u0000").toByteArray(), reqNo, appKey); cmd = cmd or Wire.ENCRYPT or 0x800L }
                    val request = Wire.Packet(reqNo, identity, "test", cmd, body).bytes()
                    s.getOutputStream().write(request)
                    val bytes = s.getInputStream().readBytes()
                    val decoded = if (secure) Cipher.getInstance("AES/CTR/NoPadding").apply { init(Cipher.DECRYPT_MODE, SecretKeySpec(secret, "AES"), IvParameterSpec(IpCrypto.iv(reqNo, true))) }.doFinal(bytes) else bytes
                    val data = if (framed) {
                        val headerSize = decoded.copyOfRange(0, 4).toString(Charsets.US_ASCII).toInt(16)
                        decoded.copyOfRange(headerSize, decoded.size)
                    } else decoded
                    assertArrayEquals(source.readBytes(), data)
                }
                await { model.state.value.transfers.find { it.id == t.id }?.status == "Sent" }
                source.delete()
                context.contentResolver.delete(sourceUri, null, null)
            }

            // Incoming plaintext file accepted explicitly, empty and non-empty.
            for ((length, secure) in listOf(0 to false, 262144 to false, 0 to true, 262144 to true)) {
                val data = ByteArray(length) { (it % 253).toByte() }
                ServerSocket(peer.localPort, 1, InetAddress.getByName("127.0.0.1")).use { server ->
                    server.soTimeout = 15_000
                    val worker = thread {
                        server.accept().use { s ->
                            s.soTimeout = 10_000
                            val raw = java.io.ByteArrayOutputStream()
                            while (true) { val b = s.getInputStream().read(); check(b >= 0); raw.write(b); if (b == 0) break }
                            val request = Wire.parse(raw.toByteArray())
                            val bytes = if (secure) {
                                val req = mock.decrypt(request.body, request.number, appKey).bytes.toString(Charsets.UTF_8).substringBefore('\u0000').split(':')
                                val secret = IpCrypto.unhex(req[4])
                                Cipher.getInstance("AES/CTR/NoPadding").apply { init(Cipher.ENCRYPT_MODE, SecretKeySpec(secret, "AES"), IvParameterSpec(IpCrypto.iv(request.number, true))) }.doFinal(data)
                            } else data
                            s.getOutputStream().write(bytes)
                            // Match Windows: retain the socket until the receiver signals completion.
                            assertEquals(-1, s.getInputStream().read())
                        }
                    }
                    val t = Transfer("incoming-$length-$secure", "127.0.0.1", 200 + length.toLong(), Wire.Attachment(0, "incoming.bin", length.toLong()), false, done = if (length > 0) 512 else 0)
                    val destination = File(context.cacheDir, "received-$length.bin")
                    // Force legacy transfer mode so explicit allowPlain is exercised.
                    model.update { it.copy(peers = it.peers.map { p -> p.copy(command = if (secure) p.command or Wire.FILE_ENC else p.command and Wire.FILE_ENC.inv()) }) }
                    engine.download(t, Uri.fromFile(destination), allowPlain = true)
                    await { model.state.value.transfers.find { it.id == t.id }?.status == "Saved" }
                    assertArrayEquals(data, destination.readBytes()); worker.join(3000); destination.delete()
                }
            }
            // A stopped sender must fail a truncated transfer instead of reporting success.
            ServerSocket(peer.localPort, 1, InetAddress.getByName("127.0.0.1")).use { server ->
                server.soTimeout = 15_000
                val worker = thread { server.accept().use { s -> while (s.getInputStream().read() > 0) { }; s.getOutputStream().write(byteArrayOf(1, 2)) } }
                model.update { it.copy(peers = it.peers.map { p -> p.copy(command = p.command and Wire.FILE_ENC.inv()) }) }
                val t = Transfer("truncated", "127.0.0.1", 999, Wire.Attachment(0, "truncated.bin", 100), false)
                val destination = File(context.cacheDir, "truncated.bin")
                engine.download(t, Uri.fromFile(destination), true)
                await { model.state.value.transfers.find { it.id == t.id }?.status?.startsWith("Failed") == true }
                worker.join(3000); destination.delete()
            }
            ServerSocket(peer.localPort, 1, InetAddress.getByName("127.0.0.1")).use { server ->
                server.soTimeout = 15_000
                val accepted = java.util.concurrent.CountDownLatch(1)
                val release = java.util.concurrent.CountDownLatch(1)
                val worker = thread { server.accept().use { s -> while (s.getInputStream().read() > 0) { }; accepted.countDown(); release.await(10, java.util.concurrent.TimeUnit.SECONDS) } }
                val t = Transfer("cancelled", "127.0.0.1", 1000, Wire.Attachment(0, "cancelled.bin", 100), false)
                val destination = File(context.cacheDir, "cancelled.bin")
                engine.download(t, Uri.fromFile(destination), true)
                assertTrue(accepted.await(15, java.util.concurrent.TimeUnit.SECONDS))
                engine.cancel(t.id)
                await { model.state.value.transfers.find { it.id == t.id }?.status == "Cancelled" }
                release.countDown(); worker.join(3000); destination.delete()
            }
            val expirySource = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, "expiry-test.bin"); put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream"); put(MediaStore.Downloads.IS_PENDING, 1)
            })!!
            context.contentResolver.openOutputStream(expirySource)!!.use { }
            context.contentResolver.update(expirySource, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
            val queued = kotlinx.coroutines.CompletableDeferred<Boolean>()
            engine.sendMessage("127.0.0.1", "expires", expirySource, onQueued = { queued.complete(it) })
            assertTrue(model.state.value.error, kotlinx.coroutines.runBlocking { kotlinx.coroutines.withTimeout(10_000) { queued.await() } })
            val expiryOffer = receive(0x20)
            transmit(45, 0x21L or Wire.UTF8, expiryOffer.number.toString())
            engine.expireOffers(System.currentTimeMillis() + 3_600_001)
            assertTrue(model.state.value.transfers.any { it.outgoing && it.packet == expiryOffer.number && it.status == "Expired" })
            context.contentResolver.delete(expirySource, null, null)
            model.clearHistory(); assertTrue(model.state.value.chats.isEmpty())
        } finally { engine.close(); model.engine=null; peer.close() }
        assertFalse(model.state.value.listening)
    }
    @Test fun historyMigrationPreservesExistingMessages() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        context.deleteDatabase("migration-test.db")
        context.openOrCreateDatabase("migration-test.db", 0, null).use { db ->
            db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, packet INTEGER, ip TEXT, body TEXT, outgoing INTEGER, security TEXT, status TEXT, time INTEGER)")
            db.execSQL("INSERT INTO messages VALUES (1,1,'192.168.1.10','keep me',0,'Plaintext','Received',1)")
            db.version = 1
        }
        History(context, "migration-test.db").use { history ->
            assertEquals("keep me", history.load().single().text)
            assertEquals("", history.load().single().peerUser)
            history.add(2, "192.168.1.10", "new", false, "Encrypted", "Received", "alice")
            assertEquals("alice", history.load().last().peerUser)
        }
        context.deleteDatabase("migration-test.db")
    }
    @Test fun duplicateProtectionSurvivesDatabaseReopenWithoutSavingPrivateMessageBody() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        context.deleteDatabase("replay-test.db")
        History(context, "replay-test.db").use { history ->
            assertTrue(history.receive(1, "127.0.0.1", "alice", "pc", "first", "Encrypted", true))
            assertTrue(history.receive(2, "127.0.0.1", "alice", "pc", "never persist this", "Encrypted", false))
        }
        History(context, "replay-test.db").use { history ->
            assertFalse(history.receive(1, "127.0.0.1", "alice", "pc", "duplicate", "Encrypted", true))
            assertFalse(history.receive(2, "127.0.0.1", "alice", "pc", "never persist this", "Encrypted", false))
            assertEquals(listOf("first"), history.load().map { it.text })
        }
        context.deleteDatabase("replay-test.db")
    }
    @Test fun v2MigrationUnreadPersistenceAndIdentityScopedConversationControls() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        context.deleteDatabase("unread-test.db")
        context.openOrCreateDatabase("unread-test.db", 0, null).use { db ->
            db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, packet INTEGER, ip TEXT, body TEXT, outgoing INTEGER, security TEXT, status TEXT, time INTEGER, peerUser TEXT NOT NULL DEFAULT '')")
            db.execSQL("INSERT INTO messages VALUES (1,1,'127.0.0.1','old history',0,'Plaintext','Received',1,'alice')")
            db.version = 2
        }
        History(context, "unread-test.db").use { history ->
            assertTrue(history.load().single().seen)
            assertTrue(history.receive(2, "127.0.0.1", "alice", "pc", "new message", "Encrypted", true))
            assertTrue(history.receive(3, "127.0.0.1", "bob", "pc", "different device", "Encrypted", true))
        }
        History(context, "unread-test.db").use { history ->
            assertEquals(2, history.load().count { !it.seen })
            history.markSeen("127.0.0.1", "alice")
            assertEquals(listOf("bob"), history.load().filter { !it.seen }.map { it.peerUser })
            history.deleteConversation("127.0.0.1", "alice")
            assertEquals("different device", history.load().single().text)
            // Clearing a conversation must not disable duplicate suppression.
            assertFalse(history.receive(2, "127.0.0.1", "alice", "pc", "new message", "Encrypted", true))
        }
        context.deleteDatabase("unread-test.db")
    }
}
