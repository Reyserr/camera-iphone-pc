package com.reyserr.lynxcam

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import kotlin.concurrent.thread
import kotlin.math.max

/** Capte le micro (44,1 kHz, mono, 16 bits) et envoie chaque morceau au PC. */
class AudioStreamer(private val onMessage: (ByteArray) -> Unit) {
    @Volatile private var running = false
    private var worker: Thread? = null

    val isRunning: Boolean get() = running

    /** La permission micro doit déjà être accordée. */
    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        if (running) return true
        val minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val record = try {
            AudioRecord(MediaRecorder.AudioSource.MIC, SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT, max(minimum, 8192))
        } catch (e: Exception) {
            AppLog.write("Micro indisponible : ${e.message}")
            return false
        }
        if (record.state != AudioRecord.STATE_INITIALIZED) {
            record.release()
            AppLog.write("Micro indisponible")
            return false
        }
        running = true
        worker = thread(name = "lynxcam-audio", isDaemon = true) {
            val buffer = ByteArray(2048)
            try {
                record.startRecording()
                while (running) {
                    val count = record.read(buffer, 0, buffer.size)
                    if (count > 0) onMessage(Wire.audio(SAMPLE_RATE, buffer, count))
                }
            } catch (e: Exception) {
                AppLog.write("Micro coupé : ${e.message}")
            } finally {
                runCatching { record.stop() }
                record.release()
                running = false
            }
        }
        AppLog.write("Micro activé")
        return true
    }

    fun stop() {
        if (!running) return
        running = false
        worker?.join(500)
        worker = null
        AppLog.write("Micro coupé")
    }

    companion object {
        const val SAMPLE_RATE = 44100
    }
}
