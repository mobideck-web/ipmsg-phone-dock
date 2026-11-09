package app.lantern.lan

import android.content.ContentValues
import android.database.sqlite.SQLiteDatabase
import android.net.Uri
import org.json.JSONArray

data class KnownDevice(val id: String, val fingerprint: String?, val ip: String, val user: String, val name: String, val approved: Boolean, val blocked: Boolean = false, val favorite: Boolean = false, val nickname: String = "")
data class OutboxItem(val id: Long, val deviceId: String, val ip: String, val user: String, val text: String, val uris: List<String>, val created: Long, val expires: Long, val state: String = "REVIEW")

object ProductSchema {
    fun create(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, fingerprint TEXT, ip TEXT NOT NULL, user TEXT NOT NULL, name TEXT NOT NULL, approved INTEGER NOT NULL, blocked INTEGER NOT NULL DEFAULT 0, favorite INTEGER NOT NULL DEFAULT 0, nickname TEXT NOT NULL DEFAULT '')")
        db.execSQL("CREATE TABLE IF NOT EXISTS outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, deviceId TEXT NOT NULL, ip TEXT NOT NULL, user TEXT NOT NULL, body TEXT NOT NULL, uris TEXT NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL, state TEXT NOT NULL DEFAULT 'REVIEW')")
        db.execSQL("CREATE TABLE IF NOT EXISTS partials (uri TEXT PRIMARY KEY, created INTEGER NOT NULL)")
        db.execSQL("CREATE TABLE IF NOT EXISTS transfer_extra (id TEXT PRIMARY KEY, phase TEXT NOT NULL, failure TEXT, detail TEXT, batchId TEXT)")
        db.execSQL("CREATE INDEX IF NOT EXISTS message_conversation ON messages(ip,peerUser,id)")
        db.execSQL("CREATE INDEX IF NOT EXISTS message_time ON messages(time)")
    }
}

class ProductRepository(private val model: Model) {
    private val db get() = model.history.writableDatabase
    fun devices(): List<KnownDevice> = db.rawQuery("SELECT id,fingerprint,ip,user,name,approved,blocked,favorite,nickname FROM devices ORDER BY favorite DESC,nickname,name", null).use { c -> buildList {
        while (c.moveToNext()) add(KnownDevice(c.getString(0), c.getString(1), c.getString(2), c.getString(3), c.getString(4), c.getInt(5) != 0, c.getInt(6) != 0, c.getInt(7) != 0, c.getString(8)))
    } }
    fun save(device: KnownDevice) {
        db.insertWithOnConflict("devices", null, ContentValues().apply {
            put("id", device.id); put("fingerprint", device.fingerprint); put("ip", device.ip); put("user", device.user); put("name", device.name)
            put("approved", device.approved); put("blocked", device.blocked); put("favorite", device.favorite); put("nickname", device.nickname.take(40))
        }, SQLiteDatabase.CONFLICT_REPLACE)
        model.changed()
    }
    fun enqueue(ip: String, user: String, text: String, uris: List<String>): Long {
        require(outbox().size < 100) { "Review or remove pending items before adding more" }
        require(uris.size <= 30 && (text.isNotBlank() || uris.isNotEmpty())) { "Choose a message or up to 30 files" }
        require(text.toByteArray(Charsets.UTF_8).size <= 6000 && '\u0000' !in text)
        val key = model.devices.identity(ip, user)
        require(devices().any { it.id == key && it.approved && !it.blocked }) { "Approve this device before queuing" }
        uris.forEach { value -> model.context.contentResolver.openInputStream(Uri.parse(value))!!.close() }
        val now = System.currentTimeMillis()
        return db.insertOrThrow("outbox", null, ContentValues().apply { put("deviceId", key); put("ip", ip); put("user", user); put("body", text); put("uris", JSONArray(uris).toString()); put("created", now); put("expires", now + 86_400_000); put("state", "REVIEW") }).also { model.changed() }
    }
    fun outbox(): List<OutboxItem> = db.rawQuery("SELECT id,deviceId,ip,user,body,uris,created,expires,state FROM outbox ORDER BY id", null).use { c -> buildList {
        while (c.moveToNext()) { val a = JSONArray(c.getString(5)); add(OutboxItem(c.getLong(0), c.getString(1), c.getString(2), c.getString(3), c.getString(4), List(a.length()) { a.getString(it) }, c.getLong(6), c.getLong(7), c.getString(8))) }
    } }
    fun removeOutbox(id: Long) { db.delete("outbox", "id=?", arrayOf(id.toString())); model.changed() }
    fun outboxState(id: Long, state: String) { db.update("outbox", ContentValues().apply { put("state", state) }, "id=?", arrayOf(id.toString())); model.changed() }
    fun partial(uri: Uri) { db.insertWithOnConflict("partials", null, ContentValues().apply { put("uri", uri.toString()); put("created", System.currentTimeMillis()) }, SQLiteDatabase.CONFLICT_REPLACE) }
    fun partials(): List<Uri> = db.rawQuery("SELECT uri FROM partials", null).use { c -> buildList { while (c.moveToNext()) add(Uri.parse(c.getString(0))) } }
    fun forgetPartial(uri: Uri) { db.delete("partials", "uri=?", arrayOf(uri.toString())) }
    fun pageFiles(ip: String,user: String,packets: List<Long>): List<Transfer> {
        if(packets.isEmpty()) return emptyList()
        return db.rawQuery("SELECT id,ip,packet,fileId,name,size,outgoing,done,status,encrypted,peerUser,savedUri,created,sourceUri FROM files WHERE ip=? AND peerUser=? AND packet IN (${packets.joinToString(","){"?"}})", (listOf(ip,user)+packets.map{it.toString()}).toTypedArray()).use {c->buildList{while(c.moveToNext()) add(model.history.hydrate(Transfer(c.getString(0),c.getString(1),c.getLong(2),Wire.Attachment(c.getLong(3),c.getString(4),c.getLong(5)),c.getInt(6)!=0,c.getLong(7),c.getString(8),c.getInt(9)!=0,c.getString(10),c.getString(11),c.getLong(12),c.getString(13))))}}
    }
    fun saveTransfer(t: Transfer) { db.insertWithOnConflict("transfer_extra", null, ContentValues().apply { put("id", t.id); put("phase", t.phase.name); put("failure", t.failure?.name); put("detail", t.detail); put("batchId", t.batchId) }, SQLiteDatabase.CONFLICT_REPLACE) }
    fun deleteMessage(chat: Chat) { model.update {it.copy(transfers=it.transfers.filterNot{f->f.ip==chat.ip && f.peerUser==chat.peerUser && f.packet==chat.packet && f.outgoing==chat.outgoing})}; db.delete("messages", "id=?", arrayOf(chat.id.toString())); db.delete("files", "ip=? AND peerUser=? AND packet=? AND outgoing=?", arrayOf(chat.ip,chat.peerUser,chat.packet.toString(),if(chat.outgoing) "1" else "0")); model.reload() }
    fun search(query: String, ip: String? = null, user: String = "", before: Long = Long.MAX_VALUE, limit: Int = 100): List<Chat> {
        val where = mutableListOf("id < ?"); val args = mutableListOf(before.toString())
        if (ip != null) { where.add("ip=? AND peerUser=?"); args.add(ip); args.add(user) }
        if (query.isNotBlank()) { where.add("(instr(lower(body),lower(?)) > 0 OR instr(ip,?) > 0)"); args.add(query); args.add(query) }
        return db.rawQuery("SELECT id,ip,body,outgoing,security,status,time,peerUser,seen,packet FROM messages WHERE ${where.joinToString(" AND ")} ORDER BY id DESC LIMIT ${limit.coerceIn(1,200)}", args.toTypedArray()).use { c -> buildList {
            while(c.moveToNext()) add(Chat(c.getLong(0),c.getString(1),c.getString(2),c.getInt(3)!=0,c.getString(4),c.getString(5),c.getLong(6),c.getString(7),c.getInt(8)!=0,c.getLong(9)))
        }.reversed() }
    }
    fun summaries(query: String): List<Chat> = db.rawQuery("SELECT m.id,m.ip,m.body,m.outgoing,m.security,m.status,m.time,m.peerUser,m.seen,m.packet FROM messages m JOIN (SELECT max(id) AS latest FROM messages WHERE instr(lower(body),lower(?))>0 OR instr(ip,?)>0 GROUP BY ip,peerUser) groups ON m.id=groups.latest ORDER BY m.id DESC LIMIT 200", arrayOf(query,query)).use { c -> buildList {
        while(c.moveToNext()) add(Chat(c.getLong(0),c.getString(1),c.getString(2),c.getInt(3)!=0,c.getString(4),c.getString(5),c.getLong(6),c.getString(7),c.getInt(8)!=0,c.getLong(9)))
    } }
}

class DevicePolicy(private val model: Model) {
    fun fingerprint(ip: String): String? = model.engine?.fingerprint(ip)?.replace(" ", "")?.lowercase()
    fun identity(ip: String, user: String): String = fingerprint(ip)?.let { "key:$it" } ?: model.repository.devices().firstOrNull { it.ip == ip && it.user == user && it.fingerprint != null }?.id ?: "legacy:$ip|$user"
    fun record(ip: String, user: String): KnownDevice? = model.repository.devices().find { it.id == identity(ip,user) }
    fun approved(ip: String, user: String): Boolean {
        val current = fingerprint(ip)
        val saved = record(ip,user) ?: return false
        return saved.approved && !saved.blocked && !model.blocked(ip) && (saved.fingerprint == null || saved.fingerprint == current)
    }
    fun approve(peer: Peer) {
        require(!model.blocked(peer.ip)) { "Restore this device first" }
        val fp = fingerprint(peer.ip)
        require(fp != null || peer.command and Wire.ENCRYPT == 0L) { "Waiting for the device key. Refresh, then verify and connect." }
        val id = fp?.let { "key:$it" } ?: "legacy:${peer.ip}|${peer.user}"
        val old = model.repository.devices().find { it.id == id }
        require(old?.blocked != true) { "Unblock this device in Settings first" }
        model.repository.save((old ?: KnownDevice(id,fp,peer.ip,peer.user,peer.name,false)).copy(ip=peer.ip,user=peer.user,name=peer.name,approved=true,blocked=false))
    }
    fun authorize(ip: String, user: String, encrypted: Boolean, plainConsent: Boolean) {
        require(approved(ip,user)) { "Device approval required. Open the chat and verify this device." }
        val saved = record(ip,user)!!
        require(saved.fingerprint == null || encrypted) { "This approved device requires encryption; refresh its key." }
        require(encrypted || (plainConsent && !model.setting("strict-messages"))) { "Plaintext sharing needs explicit confirmation" }
    }
    fun observed(peer: Peer) {
        val saved = record(peer.ip,peer.user) ?: run {
            val fp=fingerprint(peer.ip)
            if(fp==null && peer.command and Wire.ENCRYPT!=0L)return
            model.repository.save(KnownDevice(fp?.let{"key:$it"} ?: "legacy:${peer.ip}|${peer.user}",fp,peer.ip,peer.user,peer.name,false))
            return
        }
        if (saved.fingerprint != null && saved.fingerprint != fingerprint(peer.ip)) return
        if (saved.ip != peer.ip || saved.user != peer.user || saved.name != peer.name) model.repository.save(saved.copy(ip=peer.ip,user=peer.user,name=peer.name))
    }
    fun revoke(device: KnownDevice, block: Boolean = device.blocked) { model.repository.save(device.copy(approved=false,blocked=block)); model.engine?.revokeDeviceOffers(device.ip) }
    fun label(ip: String, user: String): String {
        val peer=model.state.value.peers.find{it.ip==ip && it.user==user}
        if(peer!=null) return label(peer)
        return record(ip,user)?.let{it.nickname.ifBlank{it.name}}?.takeIf{it.isNotBlank()} ?: ip
    }
    fun label(peer: Peer): String = record(peer.ip,peer.user)?.nickname?.takeIf { it.isNotBlank() } ?: peer.name
}
