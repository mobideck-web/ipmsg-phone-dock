package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream
import java.net.ServerSocket
import java.net.Socket
import kotlin.concurrent.thread
import javax.crypto.Cipher
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

class FileTransferTest {
    @Test fun windowsWaitsForReceiverToCloseAfterCompleteApkAndZip() {
        for (name in listOf("installer.apk", "archive.zip")) {
            val data = ByteArray(2 * 1024 * 1024 + 17) { (it % 251).toByte() }
            ServerSocket(0).use { server ->
                val result = java.util.concurrent.CompletableFuture<Boolean>()
                val sender = thread {
                    try { server.accept().use { socket ->
                        socket.soTimeout = 5000
                        socket.getOutputStream().write(data)
                        // Windows keeps the socket open and waits for the receiver's completion.
                        result.complete(socket.getInputStream().read() == -1)
                    } } catch (t: Throwable) { result.completeExceptionally(t) }
                }
                val output = ByteArrayOutputStream()
                Socket("127.0.0.1", server.localPort).use { socket ->
                    socket.soTimeout = 1000
                    FileTransfer.receive(socket.getInputStream(), output, data.size.toLong())
                }
                assertArrayEquals(name, data, output.toByteArray())
                assertTrue(result.get(6, java.util.concurrent.TimeUnit.SECONDS)); sender.join()
            }
        }
    }
    @Test fun emptyTransferNeverReadsSocket() {
        FileTransfer.receive(object : InputStream() { override fun read(): Int = error("Must not read for zero bytes") }, ByteArrayOutputStream(), 0)
    }
    @Test fun truncatedTransferReportsBytesAndDoesNotClaimCompletion() {
        val error = assertThrows(EOFException::class.java) { FileTransfer.receive(ByteArrayInputStream(byteArrayOf(1, 2)), ByteArrayOutputStream(), 100) }
        assertTrue(error.message!!.contains("2 of 100"))
    }
    @Test fun encryptedPayloadIsExactAcrossUnevenChunks() {
        val data = ByteArray(1024 * 1024 + 37) { (it % 239).toByte() }
        val key = SecretKeySpec(ByteArray(32) { it.toByte() }, "AES")
        fun cipher(mode: Int) = Cipher.getInstance("AES/CTR/NoPadding").apply { init(mode, key, IvParameterSpec(ByteArray(16))) }
        val encrypted = cipher(Cipher.ENCRYPT_MODE).doFinal(data)
        val input = object : ByteArrayInputStream(encrypted) {
            override fun read(b: ByteArray, off: Int, len: Int) = super.read(b, off, minOf(len, 777))
        }
        val output = ByteArrayOutputStream()
        FileTransfer.receive(input, output, encrypted.size.toLong(), cipher(Cipher.DECRYPT_MODE))
        assertArrayEquals(data, output.toByteArray())
    }
    @Test fun cancellationAndStorageFailurePropagate() {
        assertThrows(java.util.concurrent.CancellationException::class.java) {
            FileTransfer.receive(ByteArrayInputStream(ByteArray(32)), ByteArrayOutputStream(), 32, checkActive = { throw java.util.concurrent.CancellationException() })
        }
        assertThrows(java.io.IOException::class.java) {
            FileTransfer.receive(ByteArrayInputStream(ByteArray(32)), object : java.io.OutputStream() { override fun write(b: Int) { throw java.io.IOException("Storage full") } }, 32)
        }
    }
}
