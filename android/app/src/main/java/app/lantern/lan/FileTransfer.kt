package app.lantern.lan

import java.io.InputStream
import java.io.OutputStream
import java.io.EOFException
import javax.crypto.Cipher

object FileTransfer {
    /** GETFILEDATA is length-delimited. Windows waits for us to close after these bytes. */
    fun receive(input: InputStream, output: OutputStream, size: Long, cipher: Cipher? = null,
                checkActive: () -> Unit = {}, progress: (Long) -> Unit = {}) {
        require(size >= 0)
        val buffer = ByteArray(131072)
        val decoded = if (cipher != null) ByteArray(buffer.size + 32) else null
        var received = 0L
        while (received < size) {
            checkActive()
            val count = input.read(buffer, 0, minOf(buffer.size.toLong(), size - received).toInt())
            if (count <= 0) throw EOFException("Connection ended after $received of $size bytes. Ask the sender to offer the file again.")
            if (cipher == null) output.write(buffer, 0, count)
            else { val plainCount = cipher.update(buffer, 0, count, decoded!!, 0); output.write(decoded, 0, plainCount) }
            received += count
            progress(received)
        }
        checkActive()
        cipher?.doFinal()?.let { if (it.isNotEmpty()) output.write(it) }
        output.flush()
        // Do not probe EOF: Windows' complete_wait waits for the receiving socket to close.
    }
}
