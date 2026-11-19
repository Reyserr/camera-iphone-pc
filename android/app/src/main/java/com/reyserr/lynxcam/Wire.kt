package com.reyserr.lynxcam

import android.util.Log
import org.json.JSONObject
import java.nio.ByteBuffer

/**
 * Protocole LynxCam (identique à l'app iPhone, voir docs/protocole.md).
 * Message : [taille des données : 4 octets gros-boutiste][type : 1 octet][données]
 */
object Wire {
    const val VERSION = 2
    const val PORT = 5050
    const val MAX_PAYLOAD = 8_000_000

    const val HELLO: Byte = 0x01
    const val FRAME_JPEG: Byte = 0x02
    const val AUDIO_PCM: Byte = 0x04
    const val COMMAND: Byte = 0x10
    const val STATUS: Byte = 0x11
    const val LOG: Byte = 0x12
    const val BACKGROUND_IMAGE: Byte = 0x13
    const val PHOTO: Byte = 0x14
    const val PING: Byte = 0x20
    const val PONG: Byte = 0x21

    fun message(type: Byte, payload: ByteArray): ByteArray =
        ByteBuffer.allocate(5 + payload.size).putInt(payload.size).put(type).put(payload).array()

    fun json(type: Byte, value: JSONObject): ByteArray = message(type, value.toString().toByteArray(Charsets.UTF_8))

    /** Son : [fréquence : 4][voies : 1][bits : 1][options : 1][0][échantillons PCM 16 bits]. */
    fun audio(sampleRate: Int, samples: ByteArray, count: Int): ByteArray {
        val payload = ByteBuffer.allocate(8 + count)
            .putInt(sampleRate).put(1.toByte()).put(16.toByte()).put(0.toByte()).put(0.toByte())
            .put(samples, 0, count)
            .array()
        return message(AUDIO_PCM, payload)
    }
}

/** Journal : affiché sur le téléphone et envoyé au PC quand il est connecté. */
object AppLog {
    @Volatile
    var sink: ((String) -> Unit)? = null

    fun write(text: String) {
        Log.i("LynxCam", text)
        sink?.invoke(text)
    }
}
