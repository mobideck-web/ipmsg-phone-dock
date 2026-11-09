package app.lantern.lan

import android.content.Intent
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.provider.MediaStore
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.net.Socket
import java.net.URI

@RunWith(AndroidJUnit4::class)
class LiveCameraTest {
    private val instrument get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrument.targetContext
    private fun waitFor(test: () -> Boolean) {
        val end = System.currentTimeMillis() + 30_000
        while (!test()) { check(System.currentTimeMillis() < end) { "Live camera test timed out" }; Thread.sleep(100) }
    }
    private fun node(text: String): AccessibilityNodeInfo? {
        fun find(n: AccessibilityNodeInfo): AccessibilityNodeInfo? {
            if (n.text?.toString()?.equals(text, ignoreCase = true) == true) return n
            for (i in 0 until n.childCount) n.getChild(i)?.let { find(it)?.let { found -> return found } }
            return null
        }
        return instrument.uiAutomation.rootInActiveWindow?.let(::find)
    }
    private fun click(text: String) { waitFor { node(text) != null }; check(node(text)!!.performAction(AccessibilityNodeInfo.ACTION_CLICK)) }
    private fun field(activity: LiveCameraActivity, name: String): Any? = LiveCameraActivity::class.java.getDeclaredField(name).apply { isAccessible = true }.get(activity)
    private var cookie=""
    private fun http(url:String,path:String):String {
        val uri=URI(url)
        val certificate=CameraTls.load(context,"127.0.0.1").certificate
        val store=java.security.KeyStore.getInstance(java.security.KeyStore.getDefaultType()).apply{load(null);setCertificateEntry("camera",certificate)}
        val tm=javax.net.ssl.TrustManagerFactory.getInstance(javax.net.ssl.TrustManagerFactory.getDefaultAlgorithm()).apply{init(store)}
        val ssl=javax.net.ssl.SSLContext.getInstance("TLS").apply{init(null,tm.trustManagers,null)}
        fun call(route:String,token:String?=null):String = (ssl.socketFactory.createSocket(uri.host,uri.port) as javax.net.ssl.SSLSocket).use {
            it.soTimeout=10000;it.sslParameters=it.sslParameters.apply{endpointIdentificationAlgorithm="HTTPS"};it.startHandshake()
            it.outputStream.write(("${if(route=="/recording") "GET" else "POST"} $route HTTP/1.1\r\nHost: ${uri.host}:${uri.port}\r\nOrigin: https://${uri.host}:${uri.port}\r\n"+(token?.let{"X-IPMsg-Token: $it\r\n"}?:"Cookie: $cookie\r\n")+"Content-Length: 0\r\n\r\n").toByteArray());it.inputStream.readBytes().toString(Charsets.UTF_8)
        }
        if(cookie.isEmpty()) {val auth=call("/auth",uri.fragment.substringAfter("key="));cookie=Regex("Set-Cookie: ([^;]+)").find(auth)!!.groupValues[1]}
        return call(path)
    }
    @Test fun receiverCanCaptureAndRecordWithSenderAlertsAndCloseRevokesLink() {
        android.os.ParcelFileDescriptor.AutoCloseInputStream(instrument.uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.CAMERA")).use { it.readBytes() }
        if(android.os.Build.VERSION.SDK_INT>=33)android.os.ParcelFileDescriptor.AutoCloseInputStream(instrument.uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.POST_NOTIFICATIONS")).use{it.readBytes()}
        val model = Model.get(context); model.restore("127.0.0.1"); model.update { it.copy(error = null) };model.devices.approve(Peer("127.0.0.1","Camera test","live-test","pc",0))
        var activity: LiveCameraActivity? = null
        var server: LiveCameraServer? = null
        var saved: android.net.Uri? = null
        ActivityScenario.launch<LiveCameraActivity>(Intent(context, LiveCameraActivity::class.java).putExtra("peerIp", "127.0.0.1").putExtra("peerUser", "live-test")).use { scenario ->
            scenario.onActivity { activity = it }
            try {
                waitFor { check(model.state.value.error == null) { model.state.value.error.orEmpty() }; scenario.onActivity { server = field(it, "server") as? LiveCameraServer }; server?.frame != null }
                waitFor {
                    val bytes = server!!.frame
                    val bitmap = bytes?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
                    if (bitmap == null) false else {
                        val colors = (1..5).flatMap { y -> (1..5).map { x -> bitmap.getPixel(bitmap.width*x/6,bitmap.height*y/6) } }.toSet()
                        bitmap.recycle(); colors.size > 2
                    }
                }
                val frame = server!!.frame!!
                assertNotNull(BitmapFactory.decodeByteArray(frame, 0, frame.size))
                java.io.File(context.cacheDir, "live-test-frame.jpg").writeBytes(frame)
                assertTrue(http(server!!.url,"/capture").contains("image/jpeg"))
                waitFor { context.getSystemService(android.app.NotificationManager::class.java).activeNotifications.any {it.id==31 && it.notification.extras.getCharSequence(android.app.Notification.EXTRA_TEXT).toString().contains("Receiver captured a photo")} }
                assertTrue(http(server!!.url,"/record").contains("202"))
                assertNull(node("Allow recording?"))
                waitFor { var value = false; scenario.onActivity { value = field(it, "recording") == true }; value }
                waitFor { context.getSystemService(android.app.NotificationManager::class.java).activeNotifications.any {it.id==31 && it.notification.extras.getCharSequence(android.app.Notification.EXTRA_TEXT).toString().contains("Receiver started recording")} }
                val recordedFrame=server!!.frame
                waitFor { server!!.frame !== recordedFrame }
                Thread.sleep(2500)
                click("Stop recording")
                waitFor { scenario.onActivity { saved = field(it, "lastVideo") as? android.net.Uri }; saved != null }
                MediaMetadataRetriever().use { retriever -> retriever.setDataSource(context, saved!!); assertTrue(retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)!!.toLong() > 0) }
                val downloaded=http(server!!.url,"/recording")
                assertTrue(downloaded.contains("video/mp4") && downloaded.contains("ftyp"))
                scenario.onActivity { it.startActivity(Intent(it, MainActivity::class.java)) }
                waitFor { runCatching { http(server!!.url, "/record") }.isFailure }
            } finally { saved?.let { context.contentResolver.delete(it, null, null) } }
        }
    }

    @Test fun defaultDownloadFolderPublishesOnlyCompleteFilesAndKeepsDuplicateNames() {
        val model = Model.get(context)
        val old = model.preferences.getString("download-folder", null)
        model.preferences.edit().remove("download-folder").commit()
        val uris = ArrayList<android.net.Uri>()
        try {
            val name = "duplicate-${System.nanoTime()}.zip"
            repeat(2) {
                val uri = DownloadStorage.create(model, name); uris.add(uri)
                context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.RELATIVE_PATH, MediaStore.MediaColumns.IS_PENDING), null, null, null)!!.use { c -> assertTrue(c.moveToFirst()); assertEquals("Download/IPMsg/", c.getString(0)); assertEquals(1, c.getInt(1)) }
                context.contentResolver.openOutputStream(uri)!!.use { it.write(byteArrayOf(1, 2, 3)) }
                DownloadStorage.publish(model, uri)
                context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.IS_PENDING), null, null, null)!!.use { c -> assertTrue(c.moveToFirst()); assertEquals(0, c.getInt(0)) }
            }
            assertNotEquals(uris[0], uris[1])
            uris.forEach { assertArrayEquals(byteArrayOf(1, 2, 3), context.contentResolver.openInputStream(it)!!.use { stream -> stream.readBytes() }) }
        } finally { uris.forEach { context.contentResolver.delete(it, null, null) }; model.preferences.edit().putString("download-folder", old).commit() }
    }
}
