package app.lantern.lan

class StorageWriteException(cause:java.io.IOException): java.io.IOException("Could not save the file. Check free space and folder access.",cause)
class SourceChangedException: java.io.IOException("Source file changed. Share it again.")
object TransferRecovery {
    fun canRestart(t:Transfer)= !t.outgoing && t.failure !in setOf(FailureReason.IDENTITY,FailureReason.SOURCE_CHANGED) && t.phase in setOf(TransferPhase.FAILED,TransferPhase.CANCELLED,TransferPhase.INTERRUPTED)
    fun message(t:Transfer):String = when(t.failure) {
        FailureReason.TIMEOUT -> "The sender stopped responding. Keep both devices on the same network and restart."
        FailureReason.CONNECTION_ENDED -> "The connection closed before the file was complete. Restart, or ask for a fresh offer."
        FailureReason.NETWORK -> "Connection interrupted. Check Wi-Fi and the sender, then restart."
        FailureReason.PERMISSION -> "Folder or file access is unavailable. Select the folder again in Settings."
        FailureReason.STORAGE -> "The file could not be saved or read. Check free space and folder access."
        FailureReason.IDENTITY -> "The sender changed. Verify the device and request a fresh offer."
        FailureReason.SOURCE_CHANGED -> "The original file changed or disappeared. Share it again from its source."
        FailureReason.SECURITY -> t.detail ?: "The recipient requested an unencrypted file. Review Security in Settings, then retry."
        else -> if(t.outgoing) "The transfer stopped. Ask the recipient to retry while the offer is available." else "The file was not saved. Restart, or ask the sender to share it again."
    }
}
