package app.lantern.lan

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Process
import java.util.concurrent.atomic.AtomicBoolean

/** Foreground camera microphone. It never requests permission or starts itself. */
class CameraAudio(
    context: Context,
    private val onPcm: (ByteArray) -> Unit,
    private val onError: (Throwable) -> Unit
) : AutoCloseable {
    private val appContext = context.applicationContext
    private class Capture(val record: AudioRecord) {
        val active = AtomicBoolean(true)
        val released = AtomicBoolean(false)
        @Volatile var worker: Thread? = null
        fun release() { if (released.compareAndSet(false, true)) runCatching { record.release() } }
    }
    @Volatile private var capture: Capture? = null
    val running: Boolean get() = capture?.active?.get() == true

    @SuppressLint("MissingPermission")
    @Synchronized fun start(): Boolean {
        if (running) return true
        if (appContext.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return false
        var next: Capture? = null
        try {
            val minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
            check(minimum > 0) { "The microphone does not support live sound" }
            val record = AudioRecord.Builder()
                .setAudioSource(MediaRecorder.AudioSource.CAMCORDER)
                .setAudioFormat(AudioFormat.Builder().setSampleRate(SAMPLE_RATE).setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(maxOf(minimum * 2, CHUNK_BYTES * 4))
                .build()
            next = Capture(record)
            check(record.state == AudioRecord.STATE_INITIALIZED) { "The microphone could not start" }
            record.startRecording()
            check(record.recordingState == AudioRecord.RECORDSTATE_RECORDING) { "The microphone is unavailable" }
            val current = next
            capture = current
            current.worker = Thread({
                try {
                    Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO)
                    val data = ByteArray(CHUNK_BYTES)
                    while (current.active.get()) {
                        val count = record.read(data, 0, data.size, AudioRecord.READ_BLOCKING)
                        if (!current.active.get()) break
                        check(count >= 0) { "Microphone interrupted ($count)" }
                        if (count >= 2) onPcm(data.copyOf(count - count % 2))
                        else Thread.sleep(10)
                    }
                } catch (error: Throwable) {
                    if (current.active.getAndSet(false)) onError(error)
                } finally {
                    current.active.set(false)
                    runCatching { record.stop() }
                    current.release()
                    synchronized(this) { if (capture === current) capture = null }
                }
            }, "IPMsg-camera-audio").apply { isDaemon = true; start() }
            return true
        } catch (error: Throwable) {
            next?.active?.set(false)
            next?.release()
            capture = null
            onError(error)
            return false
        }
    }

    fun stop() {
        val current = synchronized(this) { val result = capture; capture = null; result } ?: return
        current.active.set(false)
        runCatching { current.record.stop() }
        current.worker?.interrupt()
        // Stop unblocks AudioRecord.read; the worker owns release and never blocks the UI.
        if (current.worker == null) current.release()
    }

    override fun close() = stop()
    companion object {
        const val SAMPLE_RATE = 16_000
        private const val CHUNK_BYTES = 1600 // 50 ms, mono PCM16 little endian.
    }
}
