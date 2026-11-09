package app.lantern.lan

import android.content.Context
import android.os.PowerManager

class TransferLease(context: Context) : AutoCloseable {
    private val lock = context.getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "IPMsg:transfer")
    init { lock.acquire(30 * 60_000L) }
    override fun close() { if (lock.isHeld) lock.release() }
}
class TransferRate {
    private var time = System.nanoTime()
    private var bytes = 0L
    fun speed(done: Long): Long {
        val now = System.nanoTime(); val elapsed = now - time
        if (elapsed < 200_000_000) return 0
        val speed = ((done - bytes).coerceAtLeast(0) * 1_000_000_000.0 / elapsed).toLong()
        time = now; bytes = done
        return speed
    }
}
