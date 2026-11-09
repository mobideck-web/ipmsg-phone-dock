package app.lantern.lan

import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class EnhancementTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    @Test fun v3MigrationPreservesHistoryAndFileLinksSurviveReopening() {
        context.deleteDatabase("v4-migration.db")
        context.openOrCreateDatabase("v4-migration.db", 0, null).use { db ->
            db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, packet INTEGER, ip TEXT, body TEXT, outgoing INTEGER, security TEXT, status TEXT, time INTEGER, peerUser TEXT NOT NULL DEFAULT '', seen INTEGER NOT NULL DEFAULT 1)")
            db.execSQL("INSERT INTO messages VALUES (1,42,'127.0.0.1','caption',0,'Encrypted','Received',1,'alice',0)")
            db.version = 3
        }
        val file = Transfer("saved", "127.0.0.1", 42, Wire.Attachment(0, "archive.zip", 123), false, 123, "Saved", true, "alice", "content://test/saved")
        History(context, "v4-migration.db").use { history ->
            assertEquals(42L, history.load().single().packet)
            assertFalse(history.load().single().seen)
            history.saveFile(file)
            history.saveFile(file.copy(id = "other-user", peerUser = "bob"))
            history.saveFile(file.copy(id = "unfinished", phase = TransferPhase.RECEIVING))
        }
        History(context, "v4-migration.db").use { history ->
            history.interruptFiles()
            assertEquals("content://test/saved", history.loadFiles().find { it.id == "saved" }!!.savedUri)
            assertEquals("Expired", history.loadFiles().find { it.id == "unfinished" }!!.status)
            history.deleteConversation("127.0.0.1", "alice")
            assertEquals("bob", history.loadFiles().single().peerUser)
            history.clear(); assertTrue(history.loadFiles().isEmpty())
        }
        context.deleteDatabase("v4-migration.db")
    }
    @Test fun cameraTargetsAllowExternalWritersAndCancelledCapturesAreDeleted() {
        for (video in listOf(false, true)) {
            val uri = FileActions.capture(context, video)
            try {
                context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.IS_PENDING, MediaStore.MediaColumns.RELATIVE_PATH), null, null, null)!!.use { cursor ->
                    assertTrue(cursor.moveToFirst()); assertEquals(0, cursor.getInt(0)); assertTrue(cursor.getString(1).contains("IPMsg"))
                }
                FileActions.finishCapture(context, uri, false)
                context.contentResolver.query(uri, null, null, null, null)?.use { assertFalse(it.moveToFirst()) }
            } finally { runCatching { context.contentResolver.delete(uri, null, null) } }
        }
    }
    @Test fun removedDevicesStayBlockedUntilRestored() {
        val model = Model.get(context)
        val ip = "192.0.2.44"
        val engine = LanEngine(model, { _, _ -> })
        try {
            model.preferences.edit().putStringSet("manual", model.preferences.getStringSet("manual", emptySet()).orEmpty() + ip).commit()
            model.update { it.copy(peers = listOf(Peer(ip, "Remove test", "test", "pc", Wire.CAPS))) }
            engine.removeDevice(ip)
            assertTrue(model.blocked(ip)); assertTrue(model.state.value.peers.none { it.ip == ip })
            assertFalse(model.preferences.getStringSet("manual", emptySet()).orEmpty().contains(ip))
            model.restore(ip); assertFalse(model.blocked(ip))
        } finally { model.restore(ip); engine.close() }
    }
    @Test fun strictMessagesAndRemovedDevicesRejectPlaintextOverRealUdp() {
        val model = Model.get(context)
        val oldStrict = model.setting("strict-messages")
        val oldPopups = model.setting("popups", true)
        val peer = java.net.DatagramSocket(0, java.net.InetAddress.getByName("127.0.0.1"))
        val port = java.net.ServerSocket(0).use { it.localPort }
        val engine = LanEngine(model, { _, _ -> }, port, peer.localPort)
        val user = "policy-${System.nanoTime()}"
        fun await(test: () -> Boolean) { val end = System.currentTimeMillis() + 30000; while (!test()) { check(System.currentTimeMillis() < end); Thread.sleep(50) } }
        fun send(n: Long, command: Long) { val bytes = Wire.Packet(n, user, "pc", command or Wire.UTF8, "policy test").bytes(); peer.send(java.net.DatagramPacket(bytes, bytes.size, java.net.InetAddress.getByName("127.0.0.1"), port)) }
        try {
            model.restore("127.0.0.1"); model.clearHistory(); model.setSetting("strict-messages", true); model.setSetting("popups", false)
            engine.start(); await { model.state.value.listening }
            send(1000, 1L or Wire.CAPS); await { model.state.value.peers.any { it.user == user } }
            send(1001, 0x120); Thread.sleep(250); assertTrue(model.history.load().isEmpty())
            model.setSetting("strict-messages", false)
            send(1002, 0x120); await { model.history.load().size == 1 }
            engine.removeDevice("127.0.0.1")
            send(1003, 0x120); Thread.sleep(250); assertEquals(1, model.history.load().size)
            model.restore("127.0.0.1"); send(1004, 1L or Wire.CAPS)
            await { model.state.value.peers.any { it.user == user } }
            send(1005, 0x120); await { model.history.load().size == 2 }
        } finally { engine.close(); peer.close(); model.restore("127.0.0.1"); model.setSetting("strict-messages", oldStrict); model.setSetting("popups", oldPopups); model.clearHistory() }
    }
}
