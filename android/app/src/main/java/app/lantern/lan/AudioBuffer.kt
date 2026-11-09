package app.lantern.lan

/** A tiny latest-first PCM queue. Slow viewers drop old sound instead of building delay. */
internal class AudioBuffer(private val capacity: Int = 8) {
    data class Packet(val sequence: Long, val bytes: ByteArray)
    private val packets = ArrayDeque<Packet>()
    private var sequence = 0L
    var epoch: Long = 0
        private set

    init { require(capacity in 1..32) }

    @Synchronized fun publish(pcm: ByteArray) {
        require(pcm.isNotEmpty() && pcm.size <= MAX_CHUNK_BYTES && pcm.size % 2 == 0)
        packets.addLast(Packet(++sequence, pcm.copyOf()))
        while (packets.size > capacity) packets.removeFirst()
    }

    @Synchronized fun latestAfter(previous: Long, expectedEpoch: Long): Packet? =
        if (expectedEpoch != epoch) null else packets.lastOrNull()?.takeIf { it.sequence > previous }

    @Synchronized fun clear(): Long {
        packets.clear()
        epoch++
        return epoch
    }

    @Synchronized fun currentEpoch(): Long = epoch
    @Synchronized fun retainedBytes(): Int = packets.sumOf { it.bytes.size }

    companion object { const val MAX_CHUNK_BYTES = 3200 }
}
