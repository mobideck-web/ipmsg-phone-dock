package app.lantern.lan

import java.net.Socket
import java.net.SocketTimeoutException
import java.io.ByteArrayOutputStream

/** Reserve packet ranges before using them, so process death cannot reuse packet IDs. */
class PacketSequence(previousReservation: Long, now: Long, private val persist: (Long) -> Unit) {
    private var current = maxOf(previousReservation + 1, now)
    private var reserved = previousReservation
    @Synchronized fun next(): Long {
        check(current <= 0xffffffffL) { "Packet number space exhausted" }
        if (current > reserved) {
            val upper = minOf(0xffffffffL, current + 255)
            persist(upper) // Must durably succeed before returning a number.
            reserved = upper
        }
        return current++
    }
}

object NetworkSafety {
    fun sameIdentity(peer: Peer?, user: String, host: String) = peer != null && peer.user == user && peer.host == host
    /** An idle timeout alone lets a client occupy a slot indefinitely by trickling bytes. */
    fun readRequest(socket: Socket, deadlineMs: Long = 10_000): ByteArray {
        val end = System.nanoTime() + deadlineMs * 1_000_000
        val oldTimeout = socket.soTimeout
        val bytes = ByteArrayOutputStream()
        try {
            while (bytes.size() < 32768) {
                val remaining = (end - System.nanoTime()) / 1_000_000
                if (remaining <= 0) throw SocketTimeoutException("File request deadline exceeded")
                socket.soTimeout = remaining.coerceAtLeast(1).toInt()
                val b = socket.getInputStream().read()
                require(b >= 0) { "Incomplete file request" }
                bytes.write(b)
                if (b == 0) return bytes.toByteArray()
            }
            throw IllegalArgumentException("File request too large")
        } finally { if (!socket.isClosed) socket.soTimeout = oldTimeout }
    }
}

/** Bounded per-address UDP work budget; preserves normal discovery and delivery retries. */
class PacketRateLimit(private val max: Int = 150, private val windowMs: Long = 10_000) {
    private data class Window(val since: Long, var count: Int)
    private val windows = LinkedHashMap<String, Window>()
    @Synchronized fun allow(ip: String, now: Long = System.nanoTime() / 1_000_000): Boolean {
        val old = windows[ip]
        if (old == null || now - old.since >= windowMs) {
            if (windows.size >= 256) windows.remove(windows.keys.first())
            windows[ip] = Window(now, 1); return true
        }
        return ++old.count <= max
    }
}
