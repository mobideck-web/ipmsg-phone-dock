package app.lantern.lan

import android.os.Bundle
import android.view.accessibility.AccessibilityNodeInfo
import android.graphics.Rect
import android.os.SystemClock
import android.view.MotionEvent
import android.view.InputDevice
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class InterfaceTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private fun eventually(test: () -> Boolean) {
        val end = System.currentTimeMillis() + 15_000
        while (!test()) { check(System.currentTimeMillis() < end) { "UI condition timed out: ${visibleText()}" }; Thread.sleep(100) }
    }
    private fun visibleText(): String {
        val values = ArrayList<String>()
        fun walk(n: AccessibilityNodeInfo) { n.text?.let { values.add(it.toString()) }; for (i in 0 until n.childCount) n.getChild(i)?.let(::walk) }
        instrumentation.uiAutomation.rootInActiveWindow?.let(::walk)
        return values.joinToString(" | ")
    }
    private fun node(text: String): AccessibilityNodeInfo? {
        fun find(n: AccessibilityNodeInfo): AccessibilityNodeInfo? {
            if (n.text?.toString() == text) return n
            for (i in 0 until n.childCount) n.getChild(i)?.let { child -> find(child)?.let { return it } }
            return null
        }
        return instrumentation.uiAutomation.rootInActiveWindow?.let(::find)
    }
    private fun click(text: String) {
        eventually { node(text) != null }
        tap(node(text)!!)
    }
    private fun tap(target: AccessibilityNodeInfo) {
        val rect = Rect(); target.getBoundsInScreen(rect)
        val now = SystemClock.uptimeMillis()
        for (action in listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP)) {
            val event = MotionEvent.obtain(now, SystemClock.uptimeMillis(), action, rect.centerX().toFloat(), rect.centerY().toFloat(), 0)
            event.source = InputDevice.SOURCE_TOUCHSCREEN
            try { assertTrue(instrumentation.uiAutomation.injectInputEvent(event, true)) } finally { event.recycle() }
        }
    }
    private fun editors(): List<AccessibilityNodeInfo> {
        val list = ArrayList<AccessibilityNodeInfo>()
        fun walk(n: AccessibilityNodeInfo) { if (n.isEditable) list.add(n); for (i in 0 until n.childCount) n.getChild(i)?.let(::walk) }
        instrumentation.uiAutomation.rootInActiveWindow?.let(::walk)
        return list
    }
    private fun setEditor(index: Int, text: String) {
        eventually { editors().size > index }
        val editor = editors()[index]
        if (!editor.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text) })) {
            tap(editor); instrumentation.sendStringSync(text)
        }
    }
    @Test fun draftSurvivesRecreationAndHistorySearchFiltersMessages() {
        val model = Model.get(instrumentation.targetContext)
        val peer = Peer("192.168.1.222", "Interface test PC", "ui-test", "pc", (Wire.CAPS and Wire.ENCRYPT.inv()))
        model.update { it.copy(listening = true, peers = listOf(peer), chats = listOf(
            Chat(10001, peer.ip, "needle message", false, "Plaintext", "Received", peerUser = peer.user),
            Chat(10002, peer.ip, "other message", false, "Plaintext", "Received", peerUser = peer.user))) }
        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity ->
                    assertSame(model, Model.get(activity))
                    model.update { it.copy(listening = true, peers = listOf(peer)) }
                }
                click("Nearby")
                click(peer.name); eventually { node("Connect") != null || node("+ Attach") != null }; if (node("Connect") != null) click("Connect")
                eventually { editors().size == 2 }
                setEditor(1, "draft survives rotation")
                eventually { node("draft survives rotation") != null }
                scenario.recreate()
                eventually { node("draft survives rotation") != null }
                click("Nearby"); click(peer.name); eventually { node("Connect") != null || node("+ Attach") != null }; if (node("Connect") != null) click("Connect")
                eventually { node("draft survives rotation") != null }
                setEditor(0, "needle")
                eventually { node("needle message") != null && node("other message") == null }
            }
        } finally { model.update { it.copy(listening = false, peers = emptyList()) }; model.reload() }
    }
    @Test fun deviceSearchUnreadAndConversationClear() {
        val model = Model.get(instrumentation.targetContext)
        val target = Peer("192.168.1.220", "Search target PC", "search-test", "target", (Wire.CAPS and Wire.ENCRYPT.inv()))
        val other = Peer("192.168.1.221", "Other nearby PC", "other-test", "other", (Wire.CAPS and Wire.ENCRYPT.inv()))
        model.clearHistory()
        model.history.receive(System.nanoTime(), target.ip, target.user, target.host, "unread test message", "Encrypted", true)
        model.reload()
        model.update { it.copy(listening = true, peers = listOf(target, other)) }
        assertFalse(model.state.value.chats.single().seen)
        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                click("Nearby")
                eventually { node(target.name) != null && node(other.name) != null }
                setEditor(0, "target")
                eventually { node(target.name) != null && node(other.name) == null }
                click("Clear search")
                eventually { node(other.name) != null }
                click(target.name); eventually { node("Connect") != null || node("unread test message") != null }; if (node("Connect") != null) click("Connect")
                eventually { model.state.value.chats.single().seen }
                assertTrue(model.history.load().single().seen)
                click("Clear"); click("Cancel")
                eventually { node("unread test message") != null }
                click("Clear"); click("Clear messages")
                eventually { model.state.value.chats.isEmpty() }
                click("‹ Back")
                eventually { node("Conversations") != null }
            }
        } finally { model.clearHistory(); model.update { it.copy(listening = false, peers = emptyList()) } }
    }
    @Test fun savedPhotoAppearsInSourceChatWithOpenFolderAndCameraActions() {
        val model = Model.get(instrumentation.targetContext)
        val peer = Peer("192.0.2.50", "Photo test PC", "photo-test", "pc", (Wire.CAPS and Wire.ENCRYPT.inv()))
        val uri = FileActions.capture(instrumentation.targetContext, false)
        try {
            instrumentation.targetContext.contentResolver.openOutputStream(uri)!!.use { stream ->
                android.graphics.Bitmap.createBitmap(32, 32, android.graphics.Bitmap.Config.ARGB_8888).apply { eraseColor(android.graphics.Color.GREEN); compress(android.graphics.Bitmap.CompressFormat.JPEG, 90, stream); recycle() }
            }
            FileActions.finishCapture(instrumentation.targetContext, uri, true)
            model.clearHistory()
            model.history.add(500, peer.ip, "photo caption", false, "Encrypted", "Received", peer.user)
            model.transfer(Transfer("photo-card", peer.ip, 500, Wire.Attachment(0, "test-photo.jpg", 100), false, 100, "Saved", true, peer.user, uri.toString()))
            model.reload(); model.update { it.copy(listening = true, error = null, peers = listOf(peer)) }
            ActivityScenario.launch(MainActivity::class.java).use {
                click("Nearby"); click(peer.name); eventually { node("Connect") != null || node("+ Attach") != null }; if (node("Connect") != null) click("Connect")
                eventually { node("test-photo.jpg") != null && node("Open") != null && node("Folder") != null }
                click("+ Attach")
                eventually { node("Take photo") != null && node("Record video") != null && node("Browse files") != null }
                click("Cancel")
                click("Files")
                eventually { node("test-photo.jpg") != null }
            }
        } finally {
            instrumentation.targetContext.contentResolver.delete(uri, null, null)
            model.clearHistory(); model.update { it.copy(listening = false, peers = emptyList()) }
        }
    }
    @Test fun settingsSwitchReflectsAndPersistsBothChanges() {
        val model = Model.get(instrumentation.targetContext)
        val old = model.setting("previews")
        fun switches(): List<AccessibilityNodeInfo> {
            val list = ArrayList<AccessibilityNodeInfo>()
            fun walk(node: AccessibilityNodeInfo) { if (node.isCheckable) list.add(node); for (i in 0 until node.childCount) node.getChild(i)?.let(::walk) }
            instrumentation.uiAutomation.rootInActiveWindow?.let(::walk)
            return list
        }
        model.setSetting("previews", false)
        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                click("Settings")
                eventually { node("My IP") != null }
                eventually { switches().size >= 3 }
                assertFalse(switches()[2].isChecked)
                tap(switches()[2]); eventually { switches()[2].isChecked }
                assertTrue(model.setting("previews"))
                tap(switches()[2]); eventually { !switches()[2].isChecked }
                assertFalse(model.setting("previews"))
            }
        } finally { model.setSetting("previews", old) }
    }

    @Test fun newDeviceRequiresConfirmationAndCameraHasSeparateConsent() {
        val model = Model.get(instrumentation.targetContext)
        val peer = Peer("192.0.2.99", "New connection test", "approval-test", "pc", (Wire.CAPS and Wire.ENCRYPT.inv()))
        val key = "${peer.ip}|${peer.user}"
        model.devices.record(peer.ip,peer.user)?.let {model.devices.revoke(it)}
        model.update { it.copy(listening = true, peers = listOf(peer), error = null, chats = listOf(
            Chat(91001, peer.ip, "Received messages are easier to read now.", false, "Encrypted", "Received", peerUser = peer.user),
            Chat(91002, peer.ip, "Yes — both sides have clear message bubbles.", true, "Encrypted", "Delivered", peerUser = peer.user)
        )) }
        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                click("Nearby"); click(peer.name); click("Cancel")
                eventually { node("Nearby devices") != null }
                assertFalse(model.devices.approved(peer.ip,peer.user))
                click(peer.name); click("Connect")
                eventually { node("+ Attach") != null }
                assertTrue(model.devices.approved(peer.ip,peer.user))
                eventually { node("Received messages are easier to read now.") != null }
                instrumentation.uiAutomation.takeScreenshot()?.let { bitmap -> java.io.File(instrumentation.targetContext.getExternalFilesDir(null), "chat-v06.png").outputStream().use { stream -> bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, stream) }; bitmap.recycle() }
                click("+ Attach"); click("Live camera")
                eventually { node("Confirm sharing") != null }
                assertTrue(visibleText().contains("The receiver can start recording and capture photos directly"))
                click("Cancel")
                eventually { node("+ Attach") != null }
            }
        } finally { model.update { it.copy(listening = false, peers = emptyList()) } }
    }
}
