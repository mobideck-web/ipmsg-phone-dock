package app.lantern.lan

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class Peer(val ip: String, val name: String, val user: String, val host: String, val command: Long, val seen: Long = System.currentTimeMillis())
data class Chat(val id: Long, val ip: String, val text: String, val outgoing: Boolean, val security: String, val status: String, val time: Long = System.currentTimeMillis(), val peerUser: String = "", val seen: Boolean = true, val packet: Long = 0)
enum class TransferPhase(val label: String) { OFFERED("Awaiting acceptance"), QUEUED("Queued"), SENDING("Sending"), RECEIVING("Receiving"), SAVED("Saved"), REMOVED("Removed from device"), SENT("Sent"), RECORDED("Saved locally"), DECLINED("Declined"), CANCELLED("Cancelled"), EXPIRED("Expired"), INTERRUPTED("Interrupted"), FAILED("Failed");
    companion object { fun legacy(value: String) = entries.firstOrNull { it.label == value } ?: if (value.startsWith("Failed")) FAILED else INTERRUPTED }
}
enum class FailureReason { NETWORK, TIMEOUT, CONNECTION_ENDED, STORAGE, PERMISSION, IDENTITY, SOURCE_CHANGED, SECURITY, UNKNOWN;
    companion object { fun of(e: Throwable) = when (e) { is StorageWriteException -> STORAGE; is SourceChangedException -> SOURCE_CHANGED; is SecurityException -> PERMISSION; is java.net.SocketTimeoutException -> TIMEOUT; is java.io.EOFException -> CONNECTION_ENDED; is java.net.SocketException -> NETWORK; is java.io.FileNotFoundException -> STORAGE; else -> UNKNOWN } }
}
data class Transfer(val id: String, val ip: String, val packet: Long, val file: Wire.Attachment, val outgoing: Boolean, val done: Long = 0, val encrypted: Boolean = false, val peerUser: String = "", val savedUri: String? = null, val created: Long = System.currentTimeMillis(), val sourceUri: String? = null, val remember: Boolean = true, val phase: TransferPhase, val failure: FailureReason? = null, val detail: String? = null, val speed: Long = 0, val batchId: String? = null) {
    val status: String get() = if (phase == TransferPhase.FAILED && detail != null) "Failed: $detail" else phase.label
    val active get() = phase in setOf(TransferPhase.QUEUED, TransferPhase.SENDING, TransferPhase.RECEIVING)
    constructor(id: String, ip: String, packet: Long, file: Wire.Attachment, outgoing: Boolean, done: Long = 0, status: String = "Awaiting acceptance", encrypted: Boolean = false, peerUser: String = "", savedUri: String? = null, created: Long = System.currentTimeMillis(), sourceUri: String? = null, remember: Boolean = true): this(id, ip, packet, file, outgoing, done, encrypted, peerUser, savedUri, created, sourceUri, remember, TransferPhase.legacy(status), detail = status.takeIf { it.startsWith("Failed: ") }?.removePrefix("Failed: "))

}

data class AppState(val listening: Boolean = false, val peers: List<Peer> = emptyList(), val chats: List<Chat> = emptyList(), val transfers: List<Transfer> = emptyList(), val error: String? = null, val revision: Int = 0, val foreground: Boolean = false)

class History(context: Context, databaseName: String = "history.db") : SQLiteOpenHelper(context, databaseName, null, 6) {
    private var receivedCount = 0
    private fun createReplayTable(db: SQLiteDatabase) { db.execSQL("CREATE TABLE IF NOT EXISTS received_packets (identity TEXT NOT NULL, packet INTEGER NOT NULL, time INTEGER NOT NULL, PRIMARY KEY(identity,packet))") }
    private fun createFilesTable(db: SQLiteDatabase) { db.execSQL("CREATE TABLE IF NOT EXISTS files (id TEXT PRIMARY KEY, ip TEXT, packet INTEGER, fileId INTEGER, name TEXT, size INTEGER, outgoing INTEGER, done INTEGER, status TEXT, encrypted INTEGER, peerUser TEXT, savedUri TEXT, created INTEGER, sourceUri TEXT)") }
    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, packet INTEGER, ip TEXT, body TEXT, outgoing INTEGER, security TEXT, status TEXT, time INTEGER, peerUser TEXT NOT NULL DEFAULT '', seen INTEGER NOT NULL DEFAULT 1)")
        createReplayTable(db)
        createFilesTable(db)
        ProductSchema.create(db)
    }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) db.execSQL("ALTER TABLE messages ADD COLUMN peerUser TEXT NOT NULL DEFAULT ''")
        if (oldVersion < 3) db.execSQL("ALTER TABLE messages ADD COLUMN seen INTEGER NOT NULL DEFAULT 1")
        createReplayTable(db)
        createFilesTable(db)
        ProductSchema.create(db)
    }
    @Synchronized fun add(packet: Long, ip: String, text: String, outgoing: Boolean, security: String, status: String, peerUser: String = "", seen: Boolean = true): Long {
        return writableDatabase.insertOrThrow("messages", null, ContentValues().apply {
            put("packet", packet); put("ip", ip); put("body", text); put("outgoing", if (outgoing) 1 else 0)
            put("security", security); put("status", status); put("time", System.currentTimeMillis())
            put("peerUser", peerUser)
            put("seen", if (seen) 1 else 0)
        })
    }
    @Synchronized fun status(packet: Long, ip: String, status: String) { writableDatabase.update("messages", ContentValues().apply { put("status", status) }, "packet=? AND ip=? AND outgoing=1", arrayOf(packet.toString(), ip)) }
    @Synchronized fun load(): List<Chat> = readableDatabase.rawQuery("SELECT id,ip,body,outgoing,security,status,time,peerUser,seen,packet FROM (SELECT * FROM messages ORDER BY id DESC LIMIT 2000) ORDER BY id", null).use { c ->
        buildList { while (c.moveToNext()) add(Chat(c.getLong(0), c.getString(1), c.getString(2), c.getInt(3) == 1, c.getString(4), c.getString(5), c.getLong(6), c.getString(7), c.getInt(8) != 0, c.getLong(9))) }
    }
    @Synchronized fun clear() { writableDatabase.delete("messages", null, null); writableDatabase.delete("files", null, null) }
    @Synchronized fun markSeen(ip: String, user: String) { writableDatabase.update("messages", ContentValues().apply { put("seen", 1) }, "ip=? AND peerUser=? AND outgoing=0 AND seen=0", arrayOf(ip, user)) }
    @Synchronized fun deleteConversation(ip: String, user: String) { writableDatabase.delete("messages", "ip=? AND peerUser=?", arrayOf(ip, user)); writableDatabase.delete("files", "ip=? AND peerUser=?", arrayOf(ip, user)) }
    @Synchronized fun saveFile(t: Transfer) {
        writableDatabase.insertWithOnConflict("files", null, ContentValues().apply {
            put("id", t.id); put("ip", t.ip); put("packet", t.packet); put("fileId", t.file.id); put("name", t.file.name); put("size", t.file.size)
            put("outgoing", if (t.outgoing) 1 else 0); put("done", t.done); put("status", t.status); put("encrypted", if (t.encrypted) 1 else 0)
            put("peerUser", t.peerUser); put("savedUri", t.savedUri); put("created", t.created); put("sourceUri", t.sourceUri)
        }, SQLiteDatabase.CONFLICT_REPLACE)

    }
    @Synchronized fun loadFiles(): List<Transfer> = readableDatabase.rawQuery("SELECT id,ip,packet,fileId,name,size,outgoing,done,status,encrypted,peerUser,savedUri,created,sourceUri FROM (SELECT * FROM files ORDER BY created DESC LIMIT 1000) ORDER BY created", null).use { c ->
        buildList { while (c.moveToNext()) add(hydrate(Transfer(c.getString(0), c.getString(1), c.getLong(2), Wire.Attachment(c.getLong(3), c.getString(4), c.getLong(5)), c.getInt(6) != 0, c.getLong(7), c.getString(8), c.getInt(9) != 0, c.getString(10), c.getString(11), c.getLong(12), c.getString(13)))) }
    }
    fun hydrate(t:Transfer):Transfer = readableDatabase.rawQuery("SELECT phase,failure,detail,batchId FROM transfer_extra WHERE id=?",arrayOf(t.id)).use { c ->
        if(!c.moveToFirst()) t else {
            val phase=runCatching{TransferPhase.valueOf(c.getString(0))}.getOrDefault(t.phase)
            t.copy(phase=if(t.phase==TransferPhase.EXPIRED && phase in setOf(TransferPhase.OFFERED,TransferPhase.QUEUED,TransferPhase.SENDING,TransferPhase.RECEIVING)) TransferPhase.EXPIRED else phase,
                failure=c.getString(1)?.let{runCatching{FailureReason.valueOf(it)}.getOrNull()},detail=c.getString(2),batchId=c.getString(3))
        }
    }
    @Synchronized fun interruptFiles() { writableDatabase.execSQL("UPDATE files SET status='Expired' WHERE status IN ('Awaiting acceptance','Sending','Receiving','Queued')") }
    @Synchronized fun interruptPending() { writableDatabase.execSQL("UPDATE messages SET status='Interrupted — send again' WHERE outgoing=1 AND status='Sending'") }
    /** Commit content and replay ID atomically, then the network layer may acknowledge. */
    @Synchronized fun receive(packet: Long, ip: String, user: String, host: String, text: String, security: String, saveContent: Boolean): Boolean {
        val db = writableDatabase
        db.beginTransaction()
        try {
            val inserted = db.insertWithOnConflict("received_packets", null, ContentValues().apply {
                put("identity", "$user:$host"); put("packet", packet); put("time", System.currentTimeMillis())
            }, SQLiteDatabase.CONFLICT_IGNORE) != -1L
            if (inserted && saveContent) add(packet, ip, text, false, security, "Received", user, seen = false)
            if (inserted && ++receivedCount % 64 == 0) db.execSQL("DELETE FROM received_packets WHERE rowid NOT IN (SELECT rowid FROM received_packets ORDER BY time DESC, rowid DESC LIMIT 8192)")
            db.setTransactionSuccessful()
            return inserted
        } finally { db.endTransaction() }
    }
}

class Model private constructor(val context: Context) {
    val preferences = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
    val history = History(context)
    val repository by lazy { ProductRepository(this) }
    val devices by lazy { DevicePolicy(this) }
    private val transientMessages = ArrayList<Chat>()
    val packetSequence = PacketSequence(preferences.getLong("packet-reserved", 0), System.currentTimeMillis() / 1000) {
        check(preferences.edit().putLong("packet-reserved", it).commit()) { "Unable to reserve packet numbers" }
    }
    private val mutable = MutableStateFlow(AppState())
    val state = mutable.asStateFlow()
    @Volatile var engine: LanEngine? = null
    var name: String
        get() = preferences.getString("name", "Android") ?: "Android"
        set(value) { preferences.edit().putString("name", value.filter { it != '\u0000' && it != ':' }.take(40).ifBlank { "Android" }).apply() }
    val user: String = preferences.getString("user", null) ?: ("lantern-" + java.util.UUID.randomUUID().toString().take(8)).also { preferences.edit().putString("user", it).apply() }
    init { history.interruptPending(); history.interruptFiles(); reload(); update { it.copy(transfers = history.loadFiles()) }
        history.writableDatabase.execSQL("UPDATE outbox SET state='REVIEW' WHERE state='SENDING'")
        val stale=repository.partials()
        Thread({DownloadStorage.cleanup(this,stale);ShareSupport.cleanup(this)},"IPMsg-partial-cleanup").start()
    }
    @Synchronized fun update(f: (AppState) -> AppState) { mutable.value = f(mutable.value) }
    fun changed() { update { it.copy(revision = it.revision + 1) } }
    @Synchronized fun reload() { val chats = (history.load() + transientMessages).sortedBy { it.time }.takeLast(2000); update { it.copy(chats = chats) } }
    @Synchronized fun ephemeral(chat: Chat) { transientMessages.add(chat); if (transientMessages.size > 100) transientMessages.removeAt(0); reload() }
    @Synchronized fun clearHistory() { history.clear(); transientMessages.clear(); update { it.copy(transfers = emptyList()) }; reload() }
    @Synchronized fun markSeen(ip: String, user: String) {
        history.markSeen(ip, user)
        transientMessages.replaceAll { if (it.ip == ip && it.peerUser == user) it.copy(seen = true) else it }
        reload()
    }
    @Synchronized fun deleteConversation(ip: String, user: String) {
        history.deleteConversation(ip, user); transientMessages.removeAll { it.ip == ip && it.peerUser == user }; reload()
        update { it.copy(transfers = it.transfers.filterNot { t -> t.ip == ip && t.peerUser == user }) }
    }
    fun error(t: Throwable) { update { it.copy(error = t.message ?: t.javaClass.simpleName) } }
    @Synchronized fun transfer(t: Transfer) {
        val old = state.value.transfers.find { it.id == t.id }
        if (t.remember && (old == null || old.phase != t.phase || old.savedUri != t.savedUri)) { history.saveFile(t); repository.saveTransfer(t) }
        update { it.copy(transfers = (it.transfers.filterNot { oldFile -> oldFile.id == t.id } + t).takeLast(1000)) }
    }
    fun setting(key: String, default: Boolean = false) = preferences.getBoolean(key, default)
    fun setSetting(key: String, value: Boolean) { preferences.edit().putBoolean(key, value).apply(); update { it.copy(revision = it.revision + 1) } }
    fun blocked(ip: String) = preferences.getStringSet("blocked", emptySet()).orEmpty().contains(ip)
    fun restore(ip: String) { preferences.edit().putStringSet("blocked", preferences.getStringSet("blocked", emptySet()).orEmpty() - ip).apply(); update { it.copy(revision = it.revision + 1) }; engine?.refresh(ip) }
    companion object {
        @Volatile private var instance: Model? = null
        fun get(context: Context): Model = instance ?: synchronized(this) { instance ?: Model(context.applicationContext).also { instance = it } }
    }
}
