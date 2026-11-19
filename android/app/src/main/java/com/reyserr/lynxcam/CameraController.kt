package com.reyserr.lynxcam

import android.Manifest
import android.annotation.SuppressLint
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CaptureRequest
import android.provider.MediaStore
import android.util.Range
import android.util.Size
import android.view.Surface
import androidx.annotation.OptIn
import androidx.camera.camera2.interop.Camera2CameraControl
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.CaptureRequestOptions
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.Camera
import androidx.camera.core.CameraInfo
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.UseCase
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.video.FallbackStrategy
import androidx.camera.video.MediaStoreOutputOptions
import androidx.camera.video.Quality
import androidx.camera.video.QualitySelector
import androidx.camera.video.Recorder
import androidx.camera.video.Recording
import androidx.camera.video.VideoCapture
import androidx.camera.video.VideoRecordEvent
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import java.util.concurrent.Executors
import kotlin.math.roundToInt

/**
 * Caméra (CameraX) : objectif, format, réglages, et enregistrement vidéo dans la galerie.
 * Les images arrivent par [onFrame] sur un fil de fond ; l'ImageProxy doit être fermé par l'appelant.
 */
@OptIn(ExperimentalCamera2Interop::class)
class CameraController(
    private val context: Context,
    private val lifecycleOwner: LifecycleOwner,
    private val previewView: PreviewView,
) {
    private val analysisExecutor = Executors.newSingleThreadExecutor()
    private var provider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var analysis: ImageAnalysis? = null
    private var videoCapture: VideoCapture<Recorder>? = null
    private var recording: Recording? = null

    var onFrame: ((ImageProxy) -> Unit)? = null
    /** Appelé après chaque changement d'objectif (fil principal). */
    var onBound: (() -> Unit)? = null
    /** Appelé quand un enregistrement se termine (fil principal). */
    var onRecordingFinished: (() -> Unit)? = null

    var kind = "back"
        private set
    var appliedFps = 30
        private set
    private var resolution = "720p"
    private var fps = 30
    private var rotation = Surface.ROTATION_0

    // Réglages de l'appareil
    private var zoom = 1.0
    private var exposure = 0.0
    private var focusAuto = true
    private var focus = 0.5
    private var whiteBalanceAuto = true
    private var kelvin = 5500

    val hasTorch: Boolean get() = camera?.cameraInfo?.hasFlashUnit() == true
    val isRecording: Boolean get() = recording != null

    fun start() {
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            provider = future.get()
            bind()
        }, ContextCompat.getMainExecutor(context))
    }

    // ───────────── Objectif et format (fil principal) ─────────────

    fun selectCamera(newKind: String) {
        kind = if (newKind in listOf("back", "ultrawide", "front")) newKind else "back"
        zoom = 1.0
        bind(withVideo = isRecording)
    }

    fun setFormat(newResolution: String, newFps: Int) {
        resolution = if (newResolution == "1080p") "1080p" else "720p"
        fps = if (newFps in listOf(15, 24, 30, 60)) newFps else 30
        if (!isRecording) bind()
    }

    fun setTargetRotation(value: Int) {
        if (value == rotation) return
        rotation = value
        analysis?.targetRotation = value
    }

    private fun bind(withVideo: Boolean = false) {
        val provider = provider ?: return
        val selector = if (kind == "front") CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA
        val info = runCatching { selector.filter(provider.availableCameraInfos).firstOrNull() }.getOrNull()
        val range = info?.let { chooseFpsRange(it, fps) }
        appliedFps = range?.upper ?: 30

        val size = if (resolution == "1080p") Size(1920, 1080) else Size(1280, 720)
        val resolutionSelector = ResolutionSelector.Builder()
            .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
            .setResolutionStrategy(ResolutionStrategy(size, ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER))
            .build()

        val previewBuilder = Preview.Builder()
        val analysisBuilder = ImageAnalysis.Builder()
            .setResolutionSelector(resolutionSelector)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .setTargetRotation(rotation)
        if (range != null) {
            Camera2Interop.Extender(previewBuilder).setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, range)
            Camera2Interop.Extender(analysisBuilder).setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, range)
        }

        val preview = previewBuilder.build()
        preview.setSurfaceProvider(previewView.surfaceProvider)
        val newAnalysis = analysisBuilder.build()
        newAnalysis.setAnalyzer(analysisExecutor) { proxy ->
            val handler = onFrame
            if (handler != null) handler(proxy) else proxy.close()
        }

        val useCases = mutableListOf<UseCase>(preview, newAnalysis)
        var newVideo: VideoCapture<Recorder>? = null
        if (withVideo) {
            val quality = if (resolution == "1080p") Quality.FHD else Quality.HD
            val recorder = Recorder.Builder()
                .setQualitySelector(QualitySelector.from(quality, FallbackStrategy.lowerQualityOrHigherThan(Quality.SD)))
                .build()
            newVideo = VideoCapture.withOutput(recorder)
            useCases.add(newVideo)
        }

        try {
            provider.unbindAll()
            camera = provider.bindToLifecycle(lifecycleOwner, selector, *useCases.toTypedArray())
            analysis = newAnalysis
            videoCapture = newVideo
            applyZoom()
            applyExposure()
            applyCamera2Options()
            AppLog.write("Caméra : ${label(kind)}, $resolution, $appliedFps img/s")
            onBound?.invoke()
        } catch (e: Exception) {
            AppLog.write("Caméra impossible : ${e.message}")
        }
    }

    /** Plage d'images/s de l'appareil la plus proche de celle demandée. */
    private fun chooseFpsRange(info: CameraInfo, wanted: Int): Range<Int>? {
        val ranges = runCatching {
            Camera2CameraInfo.from(info).getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
        }.getOrNull() ?: return null
        return ranges.firstOrNull { it.lower == wanted && it.upper == wanted }
            ?: ranges.filter { it.upper == wanted }.maxByOrNull { it.lower }
            ?: ranges.filter { it.upper < wanted }.maxWithOrNull(compareBy<Range<Int>>({ it.upper }, { it.lower }))
            ?: ranges.minByOrNull { it.upper }
    }

    // ───────────── Réglages (fil principal) ─────────────

    fun setZoom(value: Double) {
        zoom = value
        applyZoom()
    }

    private fun applyZoom() {
        val cam = camera ?: return
        val state = cam.cameraInfo.zoomState.value ?: return
        // « Ultra grand-angle » = zoom minimal de l'objectif arrière (0,5× sur les téléphones qui en ont un).
        val base = if (kind == "ultrawide") state.minZoomRatio else 1f
        val ratio = (base * zoom.toFloat()).coerceIn(state.minZoomRatio, state.maxZoomRatio)
        cam.cameraControl.setZoomRatio(ratio)
    }

    fun setTorch(on: Boolean): Boolean {
        val cam = camera ?: return false
        if (!cam.cameraInfo.hasFlashUnit()) {
            if (on) AppLog.write("Pas de lampe sur cette caméra")
            return false
        }
        cam.cameraControl.enableTorch(on)
        return on
    }

    fun setExposure(ev: Double) {
        exposure = ev
        applyExposure()
    }

    private fun applyExposure() {
        val cam = camera ?: return
        val state = cam.cameraInfo.exposureState
        if (!state.isExposureCompensationSupported) return
        val step = state.exposureCompensationStep.toFloat()
        if (step <= 0f) return
        val index = (exposure / step).roundToInt()
            .coerceIn(state.exposureCompensationRange.lower, state.exposureCompensationRange.upper)
        cam.cameraControl.setExposureCompensationIndex(index)
    }

    fun setFocus(auto: Boolean, position: Double) {
        focusAuto = auto
        focus = position.coerceIn(0.0, 1.0)
        applyCamera2Options()
    }

    fun setWhiteBalance(auto: Boolean, temperature: Int) {
        whiteBalanceAuto = auto
        kelvin = temperature
        applyCamera2Options()
    }

    /** Mise au point et balance des blancs, réglées directement sur l'appareil photo (Camera2). */
    private fun applyCamera2Options() {
        val cam = camera ?: return
        val options = CaptureRequestOptions.Builder()
        if (focusAuto) {
            options.setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
        } else {
            val minimum = runCatching {
                Camera2CameraInfo.from(cam.cameraInfo).getCameraCharacteristic(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE)
            }.getOrNull() ?: 0f
            options.setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF)
            // 0 = près (distance minimale), 1 = loin (infini). La distance est en dioptries.
            options.setCaptureRequestOption(CaptureRequest.LENS_FOCUS_DISTANCE, minimum * (1f - focus.toFloat()))
        }
        val awb = when {
            whiteBalanceAuto -> CaptureRequest.CONTROL_AWB_MODE_AUTO
            kelvin < 3300 -> CaptureRequest.CONTROL_AWB_MODE_INCANDESCENT
            kelvin < 4600 -> CaptureRequest.CONTROL_AWB_MODE_FLUORESCENT
            kelvin < 6000 -> CaptureRequest.CONTROL_AWB_MODE_DAYLIGHT
            kelvin < 7000 -> CaptureRequest.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT
            else -> CaptureRequest.CONTROL_AWB_MODE_SHADE
        }
        options.setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, awb)
        Camera2CameraControl.from(cam.cameraControl).setCaptureRequestOptions(options.build())
    }

    // ───────────── Vidéo (fil principal) ─────────────

    @SuppressLint("MissingPermission")
    fun startRecording(withAudio: Boolean) {
        if (isRecording) return
        bind(withVideo = true)
        val video = videoCapture ?: run {
            AppLog.write("Enregistrement impossible sur ce téléphone")
            return
        }
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, "LynxCam-${System.currentTimeMillis()}")
            put(MediaStore.MediaColumns.MIME_TYPE, "video/mp4")
            put(MediaStore.MediaColumns.RELATIVE_PATH, "Movies/LynxCam")
        }
        val output = MediaStoreOutputOptions.Builder(context.contentResolver, MediaStore.Video.Media.EXTERNAL_CONTENT_URI)
            .setContentValues(values)
            .build()
        var pending = video.output.prepareRecording(context, output)
        val micAllowed = ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        if (withAudio && micAllowed) pending = pending.withAudioEnabled()
        recording = pending.start(ContextCompat.getMainExecutor(context)) { event ->
            if (event is VideoRecordEvent.Finalize) {
                AppLog.write(if (event.hasError()) "Vidéo ratée (erreur ${event.error})" else "Vidéo enregistrée dans la galerie")
                recording = null
                bind(withVideo = false)
                onRecordingFinished?.invoke()
            }
        }
        AppLog.write("Enregistrement démarré${if (withAudio && micAllowed) " avec le son" else ""}")
    }

    fun stopRecording() {
        recording?.stop()
    }

    companion object {
        fun label(kind: String) = when (kind) {
            "ultrawide" -> "Ultra grand-angle"
            "front" -> "Avant"
            else -> "Arrière"
        }
    }
}
