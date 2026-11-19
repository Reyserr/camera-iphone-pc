package com.reyserr.lynxcam

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ColorMatrix
import android.graphics.ColorMatrixColorFilter
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Rect
import android.graphics.Shader
import androidx.camera.core.ImageProxy
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import kotlin.math.max
import kotlin.math.min

/** Réglages des effets, modifiés depuis le PC et lus à chaque image. */
data class EffectSettings(
    val background: String = "none", // none, blur, theme, image
    val blur: Int = 50,               // 0 à 100
    val theme: String = "nuit",
    val mirror: Boolean = false,
    val brightness: Int = 0,          // -100 à 100
    val contrast: Int = 0,
    val saturation: Int = 0,
)

/**
 * Détoure la personne (ML Kit) et applique flou, thème ou photo en arrière-plan,
 * puis les réglages de couleur et le miroir. Utilisé uniquement sur le fil d'analyse.
 */
class FrameProcessor {
    @Volatile var settings = EffectSettings()
    @Volatile private var backgroundPhoto: Bitmap? = null

    private val segmenter = Segmentation.getClient(
        SelfieSegmenterOptions.Builder()
            .setDetectorMode(SelfieSegmenterOptions.STREAM_MODE)
            .enableRawSizeMask()
            .build()
    )

    private var themeCache: Triple<String, Int, Int>? = null
    private var themeBitmap: Bitmap? = null
    private var photoCacheKey: Triple<Bitmap, Int, Int>? = null
    private var photoBitmap: Bitmap? = null
    private var columns = IntArray(0)

    fun setBackgroundPhoto(bitmap: Bitmap) {
        backgroundPhoto = bitmap
    }

    /** Image droite (tournée selon le téléphone), avec les effets. */
    fun process(proxy: ImageProxy): Bitmap {
        val raw = proxy.toBitmap()
        val rotation = proxy.imageInfo.rotationDegrees
        val s = settings

        var frame = raw
        if (rotation != 0 || s.mirror) {
            val matrix = Matrix()
            matrix.postRotate(rotation.toFloat())
            if (s.mirror) matrix.postScale(-1f, 1f)
            frame = Bitmap.createBitmap(raw, 0, 0, raw.width, raw.height, matrix, true)
        }
        if (s.background != "none") frame = applyBackground(frame, s)
        if (s.brightness != 0 || s.contrast != 0 || s.saturation != 0) frame = applyColor(frame, s)
        return frame
    }

    // ───────────── Arrière-plan ─────────────

    private fun applyBackground(frame: Bitmap, s: EffectSettings): Bitmap {
        val mask = try {
            Tasks.await(segmenter.process(InputImage.fromBitmap(frame, 0)))
        } catch (_: Exception) {
            return frame
        }
        val maskWidth = mask.width
        val maskHeight = mask.height
        val buffer = mask.buffer
        buffer.rewind()
        val confidence = FloatArray(maskWidth * maskHeight)
        buffer.asFloatBuffer().get(confidence)

        val width = frame.width
        val height = frame.height
        val backdrop = backdrop(frame, s)
        val pixels = IntArray(width * height)
        val back = IntArray(width * height)
        frame.getPixels(pixels, 0, width, 0, 0, width, height)
        backdrop.getPixels(back, 0, width, 0, 0, width, height)

        if (columns.size != width) columns = IntArray(width)
        val scaleX = maskWidth.toFloat() / width
        for (x in 0 until width) columns[x] = min(maskWidth - 1, (x * scaleX).toInt())
        val scaleY = maskHeight.toFloat() / height

        for (y in 0 until height) {
            val maskRow = min(maskHeight - 1, (y * scaleY).toInt()) * maskWidth
            val row = y * width
            for (x in 0 until width) {
                val a = confidence[maskRow + columns[x]]
                val i = row + x
                if (a >= 0.98f) continue
                if (a <= 0.02f) {
                    pixels[i] = back[i]
                    continue
                }
                val p = pixels[i]
                val b = back[i]
                val r = (((p shr 16) and 0xFF) * a + ((b shr 16) and 0xFF) * (1 - a)).toInt()
                val g = (((p shr 8) and 0xFF) * a + ((b shr 8) and 0xFF) * (1 - a)).toInt()
                val bl = ((p and 0xFF) * a + (b and 0xFF) * (1 - a)).toInt()
                pixels[i] = (0xFF shl 24) or (r shl 16) or (g shl 8) or bl
            }
        }
        val output = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        output.setPixels(pixels, 0, width, 0, 0, width, height)
        return output
    }

    private fun backdrop(frame: Bitmap, s: EffectSettings): Bitmap = when (s.background) {
        "blur" -> blurred(frame, s.blur)
        "image" -> backgroundPhoto?.let { photoBackdrop(it, frame.width, frame.height) } ?: theme(s.theme, frame.width, frame.height)
        else -> theme(s.theme, frame.width, frame.height)
    }

    /** Flou rapide : image réduite puis agrandie avec lissage. */
    private fun blurred(frame: Bitmap, amount: Int): Bitmap {
        val factor = 6 + amount.coerceIn(0, 100) * 26 / 100
        val small = Bitmap.createScaledBitmap(frame, max(1, frame.width / factor), max(1, frame.height / factor), true)
        val medium = Bitmap.createScaledBitmap(small, max(1, frame.width / 4), max(1, frame.height / 4), true)
        return Bitmap.createScaledBitmap(medium, frame.width, frame.height, true)
    }

    private fun photoBackdrop(photo: Bitmap, width: Int, height: Int): Bitmap {
        val key = Triple(photo, width, height)
        photoBitmap?.let { if (photoCacheKey == key) return it }
        val output = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val scale = max(width.toFloat() / photo.width, height.toFloat() / photo.height)
        val sourceWidth = (width / scale).toInt()
        val sourceHeight = (height / scale).toInt()
        val left = (photo.width - sourceWidth) / 2
        val top = (photo.height - sourceHeight) / 2
        Canvas(output).drawBitmap(photo, Rect(left, top, left + sourceWidth, top + sourceHeight),
            Rect(0, 0, width, height), Paint(Paint.FILTER_BITMAP_FLAG))
        photoCacheKey = key
        photoBitmap = output
        return output
    }

    private fun theme(name: String, width: Int, height: Int): Bitmap {
        val key = Triple(name, width, height)
        themeBitmap?.let { if (themeCache == key) return it }
        val output = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(output)
        val paint = Paint()
        when (name) {
            "vert" -> canvas.drawColor(Color.rgb(0, 255, 0))
            "studio" -> {
                paint.shader = RadialGradient(width / 2f, height * 0.45f, max(width, height) * 0.75f,
                    Color.rgb(0x5A, 0x5A, 0x5A), Color.rgb(0x1E, 0x1E, 0x1E), Shader.TileMode.CLAMP)
                canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), paint)
            }
            else -> {
                val (from, to) = GRADIENTS[name] ?: GRADIENTS.getValue("nuit")
                paint.shader = LinearGradient(0f, height.toFloat(), width.toFloat(), 0f, from, to, Shader.TileMode.CLAMP)
                canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), paint)
            }
        }
        themeCache = key
        themeBitmap = output
        return output
    }

    // ───────────── Couleurs ─────────────

    private fun applyColor(frame: Bitmap, s: EffectSettings): Bitmap {
        val contrast = 1f + s.contrast / 200f
        val offset = (-0.5f * contrast + 0.5f) * 255f + s.brightness / 250f * 255f
        val matrix = ColorMatrix().apply { setSaturation(1f + s.saturation / 100f) }
        matrix.postConcat(ColorMatrix(floatArrayOf(
            contrast, 0f, 0f, 0f, offset,
            0f, contrast, 0f, 0f, offset,
            0f, 0f, contrast, 0f, offset,
            0f, 0f, 0f, 1f, 0f,
        )))
        val output = Bitmap.createBitmap(frame.width, frame.height, Bitmap.Config.ARGB_8888)
        val paint = Paint().apply { colorFilter = ColorMatrixColorFilter(matrix) }
        Canvas(output).drawBitmap(frame, 0f, 0f, paint)
        return output
    }

    companion object {
        /** Mêmes thèmes et couleurs que l'app iPhone et l'app PC. */
        private val GRADIENTS = mapOf(
            "nuit" to Pair(Color.rgb(0x14, 0x1E, 0x30), Color.rgb(0x24, 0x3B, 0x55)),
            "coucher" to Pair(Color.rgb(0xFF, 0x7E, 0x5F), Color.rgb(0xFE, 0xB4, 0x7B)),
            "ocean" to Pair(Color.rgb(0x21, 0x93, 0xB0), Color.rgb(0x6D, 0xD5, 0xED)),
            "neon" to Pair(Color.rgb(0xFC, 0x00, 0xFF), Color.rgb(0x00, 0xDB, 0xDE)),
        )
    }
}
