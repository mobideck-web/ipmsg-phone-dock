package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import kotlin.concurrent.thread

class NetworkSafetyTest {
    @Test fun reservationsSurviveRestartAndClockRollback() {
        var saved = 0L
        val first = PacketSequence(saved, 1000) { saved = it }
        assertEquals(1000L, first.next()); assertEquals(1255L, saved)
        assertEquals(1001L, first.next())
        val restarted = PacketSequence(saved, 900) { saved = it }
        assertEquals(1256L, restarted.next())
    }
    @Test fun failedReservationNeverReturnsReusableNumber() {
        var fail = true
        val seq = PacketSequence(0, 100) { if (fail) error("disk full") }
        assertThrows(IllegalStateException::class.java) { seq.next() }
        fail = false
        assertEquals(100L, seq.next())
    }
    @Test fun enforcesUint32Limit() {
        val seq = PacketSequence(0xfffffffeL, 0) { }
        assertEquals(0xffffffffL, seq.next())
        assertThrows(IllegalStateException::class.java) { seq.next() }
    }
    @Test fun senderMustMatchUserAndHost() {
        val peer = Peer("127.0.0.1", "PC", "alice", "workstation", 0)
        assertTrue(NetworkSafety.sameIdentity(peer, "alice", "workstation"))
        assertFalse(NetworkSafety.sameIdentity(peer, "mallory", "workstation"))
        assertFalse(NetworkSafety.sameIdentity(peer, "alice", "impostor"))
        assertFalse(NetworkSafety.sameIdentity(null, "alice", "workstation"))
    }
    @Test fun requestMustBeTerminatedAndArriveBeforeDeadline() {
        ServerSocket(0).use { server ->
            Socket("127.0.0.1", server.localPort).use { client ->
                server.accept().use { accepted ->
                    client.getOutputStream().write("abc\u0000".toByteArray())
                    assertArrayEquals("abc\u0000".toByteArray(), NetworkSafety.readRequest(accepted, 1000))
                    assertThrows(SocketTimeoutException::class.java) { NetworkSafety.readRequest(accepted, 150) }
                }
            }
        }
    }
    @Test fun rejectsUnterminatedRequestAtEof() {
        ServerSocket(0).use { server ->
            Socket("127.0.0.1", server.localPort).use { client ->
                server.accept().use { accepted ->
                    client.getOutputStream().write("abc".toByteArray()); client.shutdownOutput()
                    assertThrows(IllegalArgumentException::class.java) { NetworkSafety.readRequest(accepted, 1000) }
                }
            }
        }
    }
    @Test fun trickledBytesCannotExtendRequestDeadline() {
        ServerSocket(0).use { server ->
            Socket("127.0.0.1", server.localPort).use { client ->
                server.accept().use { accepted ->
                    val written = java.util.concurrent.atomic.AtomicInteger()
                    val writer = thread { runCatching { repeat(30) { client.getOutputStream().write(65); written.incrementAndGet(); Thread.sleep(50) } } }
                    assertThrows(SocketTimeoutException::class.java) { NetworkSafety.readRequest(accepted, 250) }
                    assertTrue("A trickle must be rejected before its full sequence completes", written.get() < 30)
                    client.close(); writer.join(2000)
                }
            }
        }
    }
}
