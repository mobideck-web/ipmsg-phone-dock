package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test

class AudioBufferTest {
    @Test fun boundsRetentionAndDropsOldSoundForSlowViewers() {
        val buffer = AudioBuffer(capacity = 4)
        repeat(100) { buffer.publish(ByteArray(AudioBuffer.MAX_CHUNK_BYTES) { it.toByte() }) }
        assertEquals(4 * AudioBuffer.MAX_CHUNK_BYTES, buffer.retainedBytes())
        val packet = buffer.latestAfter(0, buffer.currentEpoch())!!
        assertEquals(100L, packet.sequence)
        assertNull(buffer.latestAfter(packet.sequence, buffer.currentEpoch()))
    }

    @Test fun muteClearsBacklogAndRejectsOldEpochEvenAfterReenable() {
        val buffer = AudioBuffer()
        val oldEpoch = buffer.currentEpoch()
        buffer.publish(byteArrayOf(1, 2))
        buffer.clear()
        assertEquals(0, buffer.retainedBytes())
        assertNull(buffer.latestAfter(0, buffer.currentEpoch()))
        buffer.publish(byteArrayOf(3, 4))
        assertNull(buffer.latestAfter(0, oldEpoch))
        assertArrayEquals(byteArrayOf(3, 4), buffer.latestAfter(0, buffer.currentEpoch())!!.bytes)
    }

    @Test fun ownsPayloadAndRejectsUnboundedOrMalformedPcm() {
        val buffer = AudioBuffer()
        val source = byteArrayOf(1, 2)
        buffer.publish(source)
        source[0] = 7
        assertArrayEquals(byteArrayOf(1, 2), buffer.latestAfter(0, buffer.currentEpoch())!!.bytes)
        listOf(ByteArray(0), ByteArray(3), ByteArray(AudioBuffer.MAX_CHUNK_BYTES + 2)).forEach { malformed ->
            assertTrue(runCatching { buffer.publish(malformed) }.exceptionOrNull() is IllegalArgumentException)
        }
    }
}
