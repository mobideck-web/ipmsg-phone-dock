package app.lantern.lan
import android.content.Intent
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
@RunWith(AndroidJUnit4::class)
class MaintenanceUiTest {
 @Test fun storageSelectionRequiresConfirmationAndCancelKeepsFile(){
  val instrument=InstrumentationRegistry.getInstrumentation();val context=instrument.targetContext;val model=Model.get(context)
  val old=model.preferences.getString("download-folder",null);model.preferences.edit().remove("download-folder").commit()
  val uri=DownloadStorage.create(model,"ui-storage-${System.nanoTime()}.zip",1)
  context.contentResolver.openOutputStream(uri)!!.use{it.write(7)};DownloadStorage.publish(model,uri,1)
  fun node(prefix:String):AccessibilityNodeInfo?{
   fun find(n:AccessibilityNodeInfo):AccessibilityNodeInfo?{if(n.text?.toString()?.startsWith(prefix)==true)return n;for(i in 0 until n.childCount)n.getChild(i)?.let{find(it)?.let{return it}};return null}
   return instrument.uiAutomation.rootInActiveWindow?.let(::find)
  }
  fun click(prefix:String){
   val end=System.currentTimeMillis()+30000;while(node(prefix)==null && System.currentTimeMillis()<end)Thread.sleep(100)
   val item=requireNotNull(node(prefix)){prefix};val rect=android.graphics.Rect();item.getBoundsInScreen(rect);val now=android.os.SystemClock.uptimeMillis()
   for(action in listOf(android.view.MotionEvent.ACTION_DOWN,android.view.MotionEvent.ACTION_UP)){
    val event=android.view.MotionEvent.obtain(now,android.os.SystemClock.uptimeMillis(),action,rect.centerX().toFloat(),rect.centerY().toFloat(),0)
    event.source=android.view.InputDevice.SOURCE_TOUCHSCREEN
    try{assertTrue(instrument.uiAutomation.injectInputEvent(event,true))}finally{event.recycle()}
   }
  }
  try{
   ActivityScenario.launch<MainActivity>(Intent(context,MainActivity::class.java).putExtra("openIp","192.0.2.230").putExtra("openTab","Settings")).use{
    click("Manage storage");click("Select all")
    instrument.uiAutomation.takeScreenshot()?.let{image->java.io.File(context.getExternalFilesDir(null),"storage-v070.png").outputStream().use{out->image.compress(android.graphics.Bitmap.CompressFormat.PNG,100,out)};image.recycle()}
    click("Remove selected (");click("Cancel")
    context.contentResolver.openInputStream(uri)!!.use{assertEquals(7,it.read())}
   }
  }finally{context.contentResolver.delete(uri,null,null);model.preferences.edit().apply{if(old==null)remove("download-folder")else putString("download-folder",old)}.commit()}
 }
}
