package app.lantern.lan
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
@RunWith(AndroidJUnit4::class)
class MaintenanceTest {
 private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
 @Test fun deletingDownloadPreservesChatAndClearsOnlyItsLink(){
  val model=Model.get(context);val id="storage-${System.nanoTime()}";val old=model.preferences.getString("download-folder",null)
  model.preferences.edit().remove("download-folder").commit()
  val uri=DownloadStorage.create(model,"$id.zip",3)
  var other:Uri?=null
  try{
   context.contentResolver.openOutputStream(uri)!!.use{it.write(byteArrayOf(1,2,3))};DownloadStorage.publish(model,uri,3)
   other=DownloadStorage.create(model,"$id-other.zip",1);context.contentResolver.openOutputStream(requireNotNull(other))!!.use{it.write(1)};DownloadStorage.publish(model,requireNotNull(other),1)
   val message=model.history.add(System.nanoTime(),"192.0.2.222","Retain this message",false,"Local","Received","cleanup-test")
   model.transfer(Transfer(id,"192.0.2.222",1,Wire.Attachment(0,"$id.zip",3),false,done=3,savedUri=uri.toString(),peerUser="cleanup-test",phase=TransferPhase.SAVED))
   val item=StorageManager.scan(model).items.first{it.uri==uri.toString()}
   assertEquals(1 to 0,StorageManager.remove(model,listOf(item)))
   assertTrue(model.history.load().any{it.id==message})
   assertEquals(TransferPhase.REMOVED,model.state.value.transfers.first{it.id==id}.phase)
   assertNull(model.history.loadFiles().first{it.id==id}.savedUri)
   context.contentResolver.openInputStream(requireNotNull(other))!!.use{assertEquals(1,it.read())}
  }finally{runCatching{context.contentResolver.delete(uri,null,null)};other?.let{context.contentResolver.delete(it,null,null)};model.preferences.edit().apply{if(old==null)remove("download-folder")else putString("download-folder",old)}.commit()}
 }
 @Test fun queuedAndLinkedTemporaryFilesAreProtected(){
  val model=Model.get(context);val root=File(context.filesDir,"outbox/storage-${System.nanoTime()}").apply{mkdirs()}
  val unused=File(root,"unused.bin").apply{writeBytes(byteArrayOf(2));setLastModified(System.currentTimeMillis()-7_200_000)}
  val linked=File(root,"linked.bin").apply{writeBytes(byteArrayOf(1));setLastModified(System.currentTimeMillis()-7_200_000)}
  val uri=androidx.core.content.FileProvider.getUriForFile(context,context.packageName+".files",linked).toString()
  val queued=File(root,"queued.bin").apply{writeBytes(byteArrayOf(3));setLastModified(System.currentTimeMillis()-7_200_000)}
  val queuedUri=androidx.core.content.FileProvider.getUriForFile(context,context.packageName+".files",queued).toString()
  var queuedId:Long?=null
  val id="linked-${System.nanoTime()}"
  try{
   model.transfer(Transfer(id,"192.0.2.1",1,Wire.Attachment(0,"linked.bin",1),true,sourceUri=uri,phase=TransferPhase.SENT))
   val peer=Peer("192.0.2.230","Storage queue","storage-queue","pc",0)
   model.devices.approve(peer);queuedId=model.repository.enqueue(peer.ip,peer.user,"",listOf(queuedUri))
   val scan=StorageManager.scan(model)
   assertFalse(scan.items.any{it.uri==queuedUri})
   assertEquals(0 to 1,StorageManager.remove(model,listOf(StorageItem(queuedUri,"queued.bin",1,true,queued))))
   assertFalse(scan.items.any{it.uri==uri})
   val candidate=scan.items.first{it.local==unused}
   assertEquals(1 to 0,StorageManager.remove(model,listOf(candidate)))
   assertFalse(unused.exists());assertTrue(linked.exists())
   assertEquals(0 to 1,StorageManager.remove(model,listOf(StorageItem(uri,"linked.bin",1,true,linked))))
  }finally{queuedId?.let{model.repository.removeOutbox(it)};unused.delete();linked.delete();queued.delete();root.delete();model.history.writableDatabase.delete("files","id=?",arrayOf(id))}
 }
 @Test fun failureDetailsSurviveReloadWithoutRevivingAnExpiredOffer(){
  val history=History(context,"maintenance-${System.nanoTime()}.db")
  try{
   val t=Transfer("typed","192.0.2.1",1,Wire.Attachment(0,"test.zip",10),false,phase=TransferPhase.FAILED,failure=FailureReason.TIMEOUT,detail="test")
   history.saveFile(t)
   history.writableDatabase.execSQL("INSERT INTO transfer_extra(id,phase,failure,detail) VALUES('typed','FAILED','TIMEOUT','test')")
   assertEquals(FailureReason.TIMEOUT,history.loadFiles().single().failure)
   history.writableDatabase.execSQL("UPDATE transfer_extra SET phase='SENDING'")
   history.writableDatabase.execSQL("UPDATE files SET status='Expired'")
   assertEquals(TransferPhase.EXPIRED,history.loadFiles().single().phase)
  }finally{val name=history.databaseName;history.close();context.deleteDatabase(name)}
 }
 @Test fun clearCameraModePublishesABoundedImageAndClosesItsSession(){
  val instrument=InstrumentationRegistry.getInstrumentation();val model=Model.get(context)
  android.os.ParcelFileDescriptor.AutoCloseInputStream(instrument.uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.CAMERA")).use{it.readBytes()}
  if(android.os.Build.VERSION.SDK_INT>=33)android.os.ParcelFileDescriptor.AutoCloseInputStream(instrument.uiAutomation.executeShellCommand("pm grant ${context.packageName} android.permission.POST_NOTIFICATIONS")).use{it.readBytes()}
  val old=model.preferences.getString("camera-quality",null);model.preferences.edit().putString("camera-quality","CLEAR").commit()
  model.restore("127.0.0.1");model.devices.approve(Peer("127.0.0.1","Quality test","quality-test","pc",0))
  try{
   androidx.test.core.app.ActivityScenario.launch<LiveCameraActivity>(android.content.Intent(context,LiveCameraActivity::class.java).putExtra("peerIp","127.0.0.1").putExtra("peerUser","quality-test")).use{scenario->
    var server:LiveCameraServer?=null;val deadline=System.currentTimeMillis()+60000
    while(server?.frame==null && System.currentTimeMillis()<deadline){scenario.onActivity{server=LiveCameraActivity::class.java.getDeclaredField("server").apply{isAccessible=true}.get(it) as? LiveCameraServer};Thread.sleep(100)}
    val frame=requireNotNull(server?.frame);val image=android.graphics.BitmapFactory.decodeByteArray(frame,0,frame.size)
    assertNotNull(image);assertTrue(image.width<=1280 && image.height<=1280);image.recycle()
   }
  }finally{model.preferences.edit().apply{if(old==null)remove("camera-quality")else putString("camera-quality",old)}.commit()}
 }

}
