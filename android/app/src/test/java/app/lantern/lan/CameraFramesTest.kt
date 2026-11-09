package app.lantern.lan
import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
class CameraFramesTest {
 private fun plane(bytes:ByteArray,row:Int,pixel:Int=1)=CameraFrames.Plane(ByteBuffer.wrap(bytes),row,pixel)
 @Test fun rotationsKeepPixelsAndChromaInOrder(){
  val y=plane(byteArrayOf(1,2,3,4,5,6,7,8),4);val u=plane(byteArrayOf(10,12),2);val v=plane(byteArrayOf(9,11),2)
  val expected=mapOf(0 to byteArrayOf(1,2,3,4,5,6,7,8,9,10,11,12),90 to byteArrayOf(5,1,6,2,7,3,8,4,9,10,11,12),180 to byteArrayOf(8,7,6,5,4,3,2,1,11,12,9,10),270 to byteArrayOf(4,8,3,7,2,6,1,5,11,12,9,10))
  expected.forEach{(angle,bytes)->assertArrayEquals(bytes,CameraFrames.pack(4,2,y,u,v,angle,ByteArray(12)))}
 }
 @Test fun paddedAndInterleavedPlanesRespectTheirPositions(){
  val y=plane(byteArrayOf(0,1,2,3,4,0,0,5,6,7,8,0,0),6).let{it.bytes.position(1);it}
  val u=plane(byteArrayOf(0,10,0,12),4,2).let{it.bytes.position(1);it};val v=plane(byteArrayOf(9,0,11,0),4,2)
  assertArrayEquals(byteArrayOf(1,2,3,4,5,6,7,8,9,10,11,12),CameraFrames.pack(4,2,y,u,v,0,ByteArray(12)))
 }
 @Test fun directBuffersKeepPositionsAndWorkspaceReadsFreshFrames(){
  val bytes=ByteBuffer.allocateDirect(10).apply{put(byteArrayOf(0,1,2,3,4,5,6,7,8,0));position(1);limit(9)}
  val y=CameraFrames.Plane(bytes,4,1);val u=plane(byteArrayOf(10,12),2);val v=plane(byteArrayOf(9,11),2)
  val workspace=CameraFrames.Workspace();val output=ByteArray(12)
  assertEquals(1,CameraFrames.pack(4,2,y,u,v,0,output,workspace)[0].toInt())
  bytes.put(1,42);assertEquals(42,CameraFrames.pack(4,2,y,u,v,0,output,workspace)[0].toInt())
  assertEquals(1,bytes.position());assertEquals(9,bytes.limit())
 }
}
