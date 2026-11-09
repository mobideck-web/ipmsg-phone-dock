package app.lantern.lan

import android.content.Intent
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** A bounded live endpoint for the separate Windows Edge/Chrome acceptance runner. */
@RunWith(AndroidJUnit4::class)
class BrowserInteropTest {
    @Test fun windowsBrowserWindow() {
        val instrument=InstrumentationRegistry.getInstrumentation();val context=instrument.targetContext
        android.os.ParcelFileDescriptor.AutoCloseInputStream(instrument.uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.CAMERA")).use{it.readBytes()}
        if(android.os.Build.VERSION.SDK_INT>=33)android.os.ParcelFileDescriptor.AutoCloseInputStream(instrument.uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.POST_NOTIFICATIONS")).use{it.readBytes()}
        val model=Model.get(context);model.restore("127.0.0.1");model.devices.approve(Peer("127.0.0.1","Windows browser","browser-test","pc",0))
        val directory=context.getExternalFilesDir(null)!!;val ready=File(directory,"browser-ready.json");val done=File(directory,"browser-done")
        ready.delete();done.delete()
        ActivityScenario.launch<LiveCameraActivity>(Intent(context,LiveCameraActivity::class.java).putExtra("peerIp","127.0.0.1").putExtra("peerUser","browser-test")).use{scenario->
            var server:LiveCameraServer?=null;var diagnostics="";val deadline=System.currentTimeMillis()+45000
            while(server?.frame==null){check(System.currentTimeMillis()<deadline){model.state.value.error ?: "Camera not ready; state=${scenario.state}; $diagnostics"};scenario.onActivity{server=LiveCameraActivity::class.java.getDeclaredField("server").apply{isAccessible=true}.get(it) as? LiveCameraServer;diagnostics=listOf("frameBusy","frameFallback","availableFrames","closing","camera","lastPublishedFrameAt").joinToString { name->"$name="+LiveCameraActivity::class.java.getDeclaredField(name).apply{isAccessible=true}.get(it) }};Thread.sleep(100)}
            val cert=CameraTls.load(context,"127.0.0.1").certificate
            val pin=java.util.Base64.getEncoder().encodeToString(java.security.MessageDigest.getInstance("SHA-256").digest(cert.publicKey.encoded))
            ready.writeText(org.json.JSONObject().put("url",server!!.url).put("spki",pin).toString())
            val end=System.currentTimeMillis()+120000
            while(!done.exists() && System.currentTimeMillis()<end) Thread.sleep(200)
            assertTrue("Windows browser runner must finish within two minutes",done.exists())
        }
        ready.delete();done.delete()
    }
}
