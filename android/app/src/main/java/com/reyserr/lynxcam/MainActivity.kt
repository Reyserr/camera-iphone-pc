package com.reyserr.lynxcam

import android.Manifest
import android.content.ContentValues
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.MediaStore
import android.view.OrientationEventListener
import android.view.Surface
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.ImageProxy
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : ComponentActivity() {
    private lateinit var previewView: PreviewView
    private lateinit var statusDot: View
    private lateinit var statusText: TextView
    private lateinit var fpsText: TextView
    private lateinit var addressText: TextView
    private lateinit var logText: TextView
    private lateinit var recBadge: TextView
    private lateinit var darkOverlay: View
    private lateinit var torchButton: Button
    private lateinit var micButton: Button
    private lateinit var recordButton: Button

    private val server = StreamServer()
    private val processor = FrameProcessor()
    private lateinit var camera: CameraController
    private val audio = AudioStreamer { message -> server.sendAudio(message) }
    private val handler = Handler(Looper.getMainLooper())
    private val photoRequested = AtomicBoolean(false)
    private lateinit var orientationListener: OrientationEventListener

    // État (fil principal)
    private var connected = false
    private var torchOn = false
    private var micOn = false
    private var recordStart = 0L
    private var fpsSent = 0
    private var zoom = 1.0
    private var resolution = "720p"
    private var targetFps = 30
    @Volatile private var quality = 70

    private val requestCamera = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) camera.start() else AppLog.write("Accès à la caméra refusé : autorise-le dans les paramètres Android")
    }
    private val requestMic = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) setMicrophone(true) else AppLog.write("Accès au micro refusé")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        previewView = findViewById(R.id.preview)
        statusDot = findViewById(R.id.statusDot)
        statusText = findViewById(R.id.statusText)
        fpsText = findViewById(R.id.fpsText)
        addressText = findViewById(R.id.addressText)
        logText = findViewById(R.id.logText)
        recBadge = findViewById(R.id.recBadge)
        darkOverlay = findViewById(R.id.darkOverlay)
        torchButton = findViewById(R.id.torchButton)
        micButton = findViewById(R.id.micButton)
        recordButton = findViewById(R.id.recordButton)
        previewView.scaleType = PreviewView.ScaleType.FIT_CENTER

        AppLog.sink = { text ->
            server.send(Wire.message(Wire.LOG, text.toByteArray(Charsets.UTF_8)))
            handler.post { logText.text = text }
        }

        camera = CameraController(this, this, previewView)
        camera.onFrame = { proxy -> handleFrame(proxy) }
        camera.onBound = { sendStatus() }
        camera.onRecordingFinished = {
            recordStart = 0
            if (micOn && !audio.isRunning) audio.start()
            updateButtons()
            sendStatus()
        }

        server.helloProvider = {
            JSONObject()
                .put("name", "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}")
                .put("system", "Android ${Build.VERSION.RELEASE}")
                .put("protocol", Wire.VERSION)
                .put("app", "LynxCam")
                .put("version", "2.0")
        }
        server.onCommand = { command -> handler.post { handle(command) } }
        server.onBackgroundImage = { data ->
            val bitmap = BitmapFactory.decodeByteArray(data, 0, data.size)
            if (bitmap != null) {
                processor.setBackgroundPhoto(bitmap)
                AppLog.write("Photo d'arrière-plan reçue")
            } else {
                AppLog.write("Photo d'arrière-plan illisible")
            }
        }
        server.onConnectionChange = { isConnected, _ ->
            handler.post {
                connected = isConnected
                updateStatus()
                if (isConnected) sendStatus()
            }
        }
        server.start()

        findViewById<Button>(R.id.switchButton).setOnClickListener {
            selectCamera(if (camera.kind == "front") "back" else "front")
        }
        torchButton.setOnClickListener { setTorch(!torchOn) }
        micButton.setOnClickListener { setMicrophone(!micOn) }
        findViewById<Button>(R.id.photoButton).setOnClickListener { photoRequested.set(true) }
        recordButton.setOnClickListener { setRecording(!camera.isRecording) }
        findViewById<Button>(R.id.darkButton).setOnClickListener { setDarkScreen(true) }
        darkOverlay.setOnClickListener { setDarkScreen(false) }

        // L'image envoyée reste droite, que le téléphone soit tenu en portrait ou en paysage.
        orientationListener = object : OrientationEventListener(this) {
            override fun onOrientationChanged(orientation: Int) {
                if (orientation == ORIENTATION_UNKNOWN) return
                val rotation = when (orientation) {
                    in 45 until 135 -> Surface.ROTATION_270
                    in 135 until 225 -> Surface.ROTATION_180
                    in 225 until 315 -> Surface.ROTATION_90
                    else -> Surface.ROTATION_0
                }
                camera.setTargetRotation(rotation)
            }
        }

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            camera.start()
        } else {
            requestCamera.launch(Manifest.permission.CAMERA)
        }
        updateStatus()
        updateButtons()
        handler.post(tick)
    }

    override fun onResume() {
        super.onResume()
        if (orientationListener.canDetectOrientation()) orientationListener.enable()
    }

    override fun onPause() {
        orientationListener.disable()
        if (camera.isRecording) {
            AppLog.write("App en arrière-plan : enregistrement arrêté")
            camera.stopRecording()
        }
        super.onPause()
    }

    private val tick = object : Runnable {
        override fun run() {
            fpsSent = server.takeFrameCount()
            fpsText.text = if (connected) "$fpsSent img/s" else ""
            addressText.text = "${wifiAddress() ?: "Pas de Wi-Fi"}   port ${Wire.PORT}"
            if (camera.isRecording && recordStart > 0) {
                val seconds = ((SystemClock.elapsedRealtime() - recordStart) / 1000).toInt()
                recBadge.text = String.format("● REC %02d:%02d", seconds / 60, seconds % 60)
            }
            if (connected) sendStatus()
            handler.postDelayed(this, 1000)
        }
    }

    // ───────────── Images (fil d'analyse) ─────────────

    private fun handleFrame(proxy: ImageProxy) {
        val streaming = server.wantsFrame()
        val photo = photoRequested.getAndSet(false)
        if (!streaming && !photo) {
            proxy.close()
            return
        }
        val timestamp = proxy.imageInfo.timestamp / 1_000_000
        val frame: Bitmap? = try {
            processor.process(proxy)
        } catch (e: Exception) {
            null
        } finally {
            proxy.close()
        }
        if (frame == null) return
        if (photo) savePhoto(frame)
        if (streaming) {
            val out = ByteArrayOutputStream(160_000)
            frame.compress(Bitmap.CompressFormat.JPEG, quality, out)
            server.sendFrame(out.toByteArray(), timestamp)
        }
    }

    private fun savePhoto(frame: Bitmap) {
        val out = ByteArrayOutputStream()
        frame.compress(Bitmap.CompressFormat.JPEG, 95, out)
        val bytes = out.toByteArray()
        server.send(Wire.message(Wire.PHOTO, bytes))
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, "LynxCam-${System.currentTimeMillis()}.jpg")
            put(MediaStore.MediaColumns.MIME_TYPE, "image/jpeg")
            put(MediaStore.MediaColumns.RELATIVE_PATH, "Pictures/LynxCam")
        }
        val saved = runCatching {
            val uri = contentResolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)
            uri?.let { contentResolver.openOutputStream(it)?.use { stream -> stream.write(bytes) } } != null
        }.getOrDefault(false)
        AppLog.write(if (saved) "Photo enregistrée dans la galerie" else "Photo non enregistrée")
    }

    // ───────────── Actions (téléphone ou PC, fil principal) ─────────────

    private fun selectCamera(kind: String) {
        torchOn = false
        zoom = 1.0
        camera.selectCamera(kind)
        updateButtons()
    }

    private fun setTorch(on: Boolean) {
        torchOn = camera.setTorch(on)
        updateButtons()
        sendStatus()
    }

    private fun setMicrophone(on: Boolean) {
        if (on && ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestMic.launch(Manifest.permission.RECORD_AUDIO)
            return
        }
        micOn = on
        if (on) {
            // Pendant un enregistrement, le micro sert à la vidéo ; le son reprend vers le PC à la fin.
            if (!camera.isRecording) micOn = audio.start()
        } else {
            audio.stop()
        }
        updateButtons()
        sendStatus()
    }

    private fun setRecording(on: Boolean) {
        if (on) {
            if (micOn) audio.stop()
            camera.startRecording(withAudio = micOn)
            recordStart = if (camera.isRecording) SystemClock.elapsedRealtime() else 0
        } else {
            camera.stopRecording()
        }
        updateButtons()
        sendStatus()
    }

    private fun setDarkScreen(on: Boolean) {
        darkOverlay.visibility = if (on) View.VISIBLE else View.GONE
        window.attributes = window.attributes.apply {
            screenBrightness = if (on) 0.01f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        }
    }

    private fun handle(command: JSONObject) {
        val name = command.optString("cmd")
        val value = command.opt("value")
        val number = (value as? Number)?.toDouble()
        val flag = value as? Boolean
        val text = value as? String
        when (name) {
            "camera" -> text?.let { selectCamera(it) }
            "torch" -> flag?.let { setTorch(it) }
            "zoom" -> number?.let { zoom = it; camera.setZoom(it) }
            "resolution" -> text?.let { resolution = if (it == "1080p") "1080p" else "720p"; camera.setFormat(resolution, targetFps) }
            "fps" -> number?.let { targetFps = it.toInt(); camera.setFormat(resolution, targetFps) }
            "quality" -> number?.let { quality = (it * 100).toInt().coerceIn(10, 100) }
            "background" -> text?.let { processor.settings = processor.settings.copy(background = it) }
            "blur" -> number?.let { processor.settings = processor.settings.copy(blur = it.toInt()) }
            "theme" -> text?.let { processor.settings = processor.settings.copy(theme = it) }
            "mirror" -> flag?.let { processor.settings = processor.settings.copy(mirror = it) }
            "brightness" -> number?.let { processor.settings = processor.settings.copy(brightness = it.toInt()) }
            "contrast" -> number?.let { processor.settings = processor.settings.copy(contrast = it.toInt()) }
            "saturation" -> number?.let { processor.settings = processor.settings.copy(saturation = it.toInt()) }
            "exposure" -> number?.let { camera.setExposure(it) }
            "focus" -> camera.setFocus(auto = text == "auto" || number == null, position = number ?: 0.5)
            "whitebalance" -> camera.setWhiteBalance(auto = text == "auto" || number == null, temperature = number?.toInt() ?: 5500)
            "mic" -> flag?.let { setMicrophone(it) }
            "record" -> flag?.let { setRecording(it) }
            "photo" -> photoRequested.set(true)
            else -> AppLog.write("Commande inconnue : $name")
        }
        sendStatus()
    }

    private fun sendStatus() {
        val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = battery?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
        val state = battery?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        val recording = camera.isRecording
        val seconds = if (recording && recordStart > 0) (SystemClock.elapsedRealtime() - recordStart) / 1000 else 0
        val status = JSONObject()
            .put("camera", camera.kind)
            .put("torch", torchOn)
            .put("zoom", zoom)
            .put("resolution", resolution)
            .put("fps", fpsSent)
            .put("fpsTarget", targetFps)
            .put("fpsApplied", camera.appliedFps)
            .put("quality", quality / 100.0)
            .put("mic", micOn)
            .put("recording", recording)
            .put("recordSeconds", seconds)
            .put("background", processor.settings.background)
            .put("battery", if (level >= 0 && scale > 0) level.toDouble() / scale else -1.0)
            .put("charging", state == BatteryManager.BATTERY_STATUS_CHARGING || state == BatteryManager.BATTERY_STATUS_FULL)
        server.send(Wire.json(Wire.STATUS, status))
    }

    // ───────────── Écran ─────────────

    private fun updateStatus() {
        statusText.text = if (connected) "Connecté au PC" else "En attente du PC…"
        statusDot.background.mutate().setTint(if (connected) Color.rgb(0x4A, 0xDE, 0x80) else Color.rgb(0xF5, 0x9E, 0x0B))
    }

    private fun updateButtons() {
        torchButton.isActivated = torchOn
        micButton.isActivated = micOn
        micButton.text = if (micOn) "🎙" else "🔇"
        val recording = camera.isRecording
        recordButton.isActivated = recording
        recordButton.text = if (recording) "⏹" else "⏺"
        recBadge.visibility = if (recording) View.VISIBLE else View.GONE
    }

    private fun wifiAddress(): String? = runCatching {
        val interfaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().filter { it.isUp && !it.isLoopback }
        val wifi = interfaces.firstOrNull { it.name.startsWith("wlan") } ?: interfaces.firstOrNull()
        val candidates = (listOfNotNull(wifi) + interfaces).flatMap { it.inetAddresses.toList() }
        candidates.filterIsInstance<Inet4Address>().firstOrNull { it.isSiteLocalAddress }?.hostAddress
    }.getOrNull()

    override fun onDestroy() {
        handler.removeCallbacks(tick)
        audio.stop()
        AppLog.sink = null
        super.onDestroy()
    }
}
