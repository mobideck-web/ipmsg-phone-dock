package app.lantern.lan
import org.junit.Assert.*
import org.junit.Test
class RecoveryTest {
 private fun transfer(phase:TransferPhase=TransferPhase.FAILED,reason:FailureReason?=FailureReason.NETWORK,outgoing:Boolean=false)=Transfer("recovery","192.0.2.9",1,Wire.Attachment(0,"test.zip",100),outgoing,done=50,phase=phase,failure=reason)
 @Test fun typedFailuresDistinguishTimeoutTruncationAndChangedSource(){
  assertEquals(FailureReason.TIMEOUT,FailureReason.of(java.net.SocketTimeoutException()))
  assertEquals(FailureReason.CONNECTION_ENDED,FailureReason.of(java.io.EOFException()))
  assertEquals(FailureReason.SOURCE_CHANGED,FailureReason.of(SourceChangedException()))
 }
 @Test fun restartNeverRevivesExpiredOrOutgoingOffersAndNeedsFreshIdentity(){
  assertTrue(TransferRecovery.canRestart(transfer()))
  assertTrue(TransferRecovery.canRestart(transfer(TransferPhase.CANCELLED,null)))
  assertFalse(TransferRecovery.canRestart(transfer(TransferPhase.EXPIRED)))
  assertFalse(TransferRecovery.canRestart(transfer(outgoing=true)))
  assertFalse(TransferRecovery.canRestart(transfer(reason=FailureReason.IDENTITY)))
  assertFalse(TransferRecovery.canRestart(transfer(reason=FailureReason.SOURCE_CHANGED)))
 }
 @Test fun qualityDefaultsAreBoundedAndSmoothUsesLessData(){
  assertEquals(CameraQuality.SMOOTH,CameraQuality.from("unknown"))
  assertTrue(CameraQuality.SMOOTH.width*CameraQuality.SMOOTH.height<CameraQuality.CLEAR.width*CameraQuality.CLEAR.height)
  assertTrue(CameraQuality.entries.all{it.intervalMs>=80 && it.jpegQuality in 40..80 && it.width<=1280})
 }
}
