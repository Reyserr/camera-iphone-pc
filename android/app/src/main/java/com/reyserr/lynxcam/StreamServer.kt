package com.reyserr.lynxcam

import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread

/**
 * Serveur TCP : attend l'app PC (Wi-Fi, ou câble USB via adb) et lui envoie les images.
 * Un seul PC à la fois : une nouvelle connexion remplace l'ancienne.
 */
class StreamServer {
    private val sender = Executors.newSingleThreadExecutor()
    private val frameInFlight = AtomicBoolean(false)
    private val pendingAudio = AtomicInteger(0)
    private val framesSent = AtomicInteger(0)

    @Volatile private var socket: Socket? = null
    @Volatile private var output: OutputStream? = null

    var onCommand: ((JSONObject) -> Unit)? = null
    var onBackgroundImage: ((ByteArray) -> Unit)? = null
    var onConnectionChange: ((Boolean, String) -> Unit)? = null
    var helloProvider: (() -> JSONObject)? = null

    val isConnected: Boolean get() = socket != null

    fun start() {
        thread(name = "lynxcam-server", isDaemon = true) { acceptLoop() }
    }

    private fun acceptLoop() {
        while (true) {
            try {
                ServerSocket().use { server ->
                    server.reuseAddress = true
                    server.bind(InetSocketAddress(Wire.PORT))
                    AppLog.write("En attente du PC sur le port ${Wire.PORT}")
                    while (true) {
                        val client = server.accept()
                        client.tcpNoDelay = true
                        accept(client)
                    }
                }
            } catch (e: Exception) {
                AppLog.write("Serveur arrêté : ${e.message}")
                Thread.sleep(2000)
            }
        }
    }

    private fun accept(client: Socket) {
        socket?.let {
            AppLog.write("Nouveau PC : l'ancienne connexion est fermée")
            closeQuietly(it)
        }
        output = BufferedOutputStream(client.getOutputStream(), 1 shl 16)
        socket = client
        frameInFlight.set(false)
        val peer = client.remoteSocketAddress.toString().trimStart('/')
        AppLog.write("PC connecté : $peer")
        onConnectionChange?.invoke(true, peer)
        helloProvider?.invoke()?.let { send(Wire.json(Wire.HELLO, it)) }
        thread(name = "lynxcam-reader", isDaemon = true) { readLoop(client) }
    }

    private fun readLoop(client: Socket) {
        try {
            val input = DataInputStream(BufferedInputStream(client.getInputStream()))
            while (true) {
                val length = input.readInt()
                val type = input.readByte()
                if (length < 0 || length > Wire.MAX_PAYLOAD) throw IOException("message trop grand ($length octets)")
                val payload = ByteArray(length)
                input.readFully(payload)
                when (type) {
                    Wire.PING -> send(Wire.message(Wire.PONG, payload))
                    Wire.COMMAND -> runCatching { JSONObject(String(payload, Charsets.UTF_8)) }
                        .getOrNull()?.let { onCommand?.invoke(it) }
                    Wire.BACKGROUND_IMAGE -> onBackgroundImage?.invoke(payload)
                }
            }
        } catch (_: Exception) {
            // Connexion terminée.
        } finally {
            if (socket === client) {
                socket = null
                output = null
                frameInFlight.set(false)
                AppLog.write("PC déconnecté")
                onConnectionChange?.invoke(false, "")
            }
            closeQuietly(client)
        }
    }

    /** Vrai si un PC est connecté et que l'image précédente est partie. */
    fun wantsFrame(): Boolean = socket != null && !frameInFlight.get()

    fun sendFrame(jpeg: ByteArray, timestampMs: Long) {
        if (socket == null || !frameInFlight.compareAndSet(false, true)) return
        val payload = ByteBuffer.allocate(8 + jpeg.size).putLong(timestampMs).put(jpeg).array()
        val message = Wire.message(Wire.FRAME_JPEG, payload)
        sender.execute {
            if (write(message)) framesSent.incrementAndGet()
            frameInFlight.set(false)
        }
    }

    fun sendAudio(message: ByteArray) {
        if (socket == null || pendingAudio.get() > 20) return
        pendingAudio.incrementAndGet()
        sender.execute {
            write(message)
            pendingAudio.decrementAndGet()
        }
    }

    fun send(message: ByteArray) {
        if (socket == null) return
        sender.execute { write(message) }
    }

    /** Images envoyées depuis le dernier appel (pour les images/s). */
    fun takeFrameCount(): Int = framesSent.getAndSet(0)

    private fun write(message: ByteArray): Boolean {
        val out = output ?: return false
        return try {
            out.write(message)
            out.flush()
            true
        } catch (_: IOException) {
            socket?.let { closeQuietly(it) }
            false
        }
    }

    private fun closeQuietly(s: Socket) {
        try {
            s.close()
        } catch (_: Exception) {
        }
    }
}
