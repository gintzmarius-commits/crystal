package com.liquidisland

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.ColorSpace
import android.graphics.PixelFormat
import android.hardware.HardwareBuffer
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.util.Log
import android.view.Choreographer
import android.view.WindowManager

/**
 * Capture de l'ecran EN DIRECT (comme un enregistreur d'ecran) pour l'effet goutte d'eau.
 *
 * Optimisations pour les petits telephones :
 * - l'ecran est copie en resolution reduite ([SCALE]) ;
 * - les images restent en memoire graphique (aucune copie par le processeur) ;
 * - a chaque image, on lit seulement quelques points AUTOUR de l'ilot : si rien n'a change,
 *   l'image est ignoree et l'ilot n'est pas redessine (sinon il se redessinerait en boucle,
 *   puisqu'il fait lui-meme partie de l'ecran capture) ;
 * - capture en pause quand l'ecran est eteint.
 */
/** A utiliser seulement a partir d'Android 13 (API 33). */
class LiveCapture(private val ctx: Context, private val main: Handler, private val listener: Listener) {

    interface Listener {
        /** Nouvelle image du fond. [sx], [sy] : echelle ecran -> image. */
        fun onLiveFrame(bmp: Bitmap, sx: Float, sy: Float)
        /** Points autour de l'ilot (x, y en pixels d'ecran) a surveiller ; null = toujours redessiner. */
        fun ringPoints(): FloatArray?
        /** La capture s'est arretee (refus, arret par l'utilisateur, ecran verrouille...). */
        fun onLiveStopped()
    }

    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var cpuReadable = false
    private var w = 0
    private var h = 0
    private var screenW = 1
    private var screenH = 1

    private var shown: Image? = null
    private var shownHb: HardwareBuffer? = null
    private var shownBmp: Bitmap? = null
    private var lastHash = 0L
    private var paused = false

    /** Forcer l'utilisation de la prochaine image (forme de l'ilot changee, reprise...). */
    @Volatile var force = true

    val active get() = projection != null
    var received = 0
    var used = 0

    fun start(resultCode: Int, data: Intent): Boolean {
        if (projection != null) return true
        val mpm = ctx.getSystemService(MediaProjectionManager::class.java)
        val p = try { mpm.getMediaProjection(resultCode, data) } catch (e: Exception) {
            Log.w(TAG, "Capture refusee : $e"); null
        } ?: return false
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                main.post {
                    if (projection != null) {
                        Log.i(TAG, "Capture en direct arretee par le systeme")
                        release()
                        listener.onLiveStopped()
                    }
                }
            }
        }, main)
        projection = p
        val (dw, dh, dpi) = size()
        val r = newReader(dw, dh)
        reader = r
        display = try {
            p.createVirtualDisplay(
                "WaveIsland", dw, dh, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, r.surface, null, main
            )
        } catch (e: Exception) {
            Log.w(TAG, "Ecran virtuel impossible : $e")
            release()
            return false
        }
        force = true
        Log.i(TAG, "Effet goutte en direct actif (${dw}x$dh, lecture rapide=$cpuReadable)")
        return true
    }

    private fun size(): Triple<Int, Int, Int> {
        val wm = ctx.getSystemService(WindowManager::class.java)
        val b = wm.maximumWindowMetrics.bounds
        screenW = b.width()
        screenH = b.height()
        val dw = ((screenW * SCALE).toInt() / 2) * 2
        val dh = ((screenH * SCALE).toInt() / 2) * 2
        val dpi = (ctx.resources.displayMetrics.densityDpi * SCALE).toInt().coerceAtLeast(80)
        w = dw
        h = dh
        return Triple(dw, dh, dpi)
    }

    private fun newReader(dw: Int, dh: Int): ImageReader {
        val r = try {
            ImageReader.newInstance(
                dw, dh, PixelFormat.RGBA_8888, MAX_IMAGES,
                HardwareBuffer.USAGE_GPU_SAMPLED_IMAGE or HardwareBuffer.USAGE_CPU_READ_OFTEN
            ).also { cpuReadable = true }
        } catch (e: Exception) {
            cpuReadable = false
            ImageReader.newInstance(dw, dh, PixelFormat.RGBA_8888, MAX_IMAGES, HardwareBuffer.USAGE_GPU_SAMPLED_IMAGE)
        }
        r.setOnImageAvailableListener({ onImage(it) }, main)
        return r
    }

    private fun onImage(r: ImageReader) {
        if (r !== reader) return
        val img = try { r.acquireLatestImage() } catch (e: Exception) { null } ?: return
        received++
        if (paused) { img.close(); return }
        // Rien n'a bouge autour de l'ilot : on ignore l'image (pas de redessin inutile)
        if (cpuReadable) {
            val hsh = ringHash(img)
            if (!force && hsh == lastHash) { img.close(); return }
            lastHash = hsh
        }
        force = false
        val hb = img.hardwareBuffer
        if (hb == null) { img.close(); return }
        val bmp = Bitmap.wrapHardwareBuffer(hb, ColorSpace.get(ColorSpace.Named.SRGB))
        if (bmp == null) { hb.close(); img.close(); return }
        used++
        listener.onLiveFrame(bmp, w.toFloat() / screenW, h.toFloat() / screenH)
        val oldImg = shown
        val oldHb = shownHb
        val oldBmp = shownBmp
        shown = img
        shownHb = hb
        shownBmp = bmp
        if (oldImg != null) releaseLater(oldImg, oldHb, oldBmp)
    }

    /** Lit quelques points autour de l'ilot (lecture directe, sans copie de l'image). */
    private fun ringHash(img: Image): Long {
        val pts = listener.ringPoints() ?: return System.nanoTime()
        return try {
            val plane = img.planes[0]
            val buf = plane.buffer
            val row = plane.rowStride
            val px = plane.pixelStride
            val sx = w.toFloat() / screenW
            val sy = h.toFloat() / screenH
            var hsh = 1125899906842597L
            var i = 0
            while (i + 1 < pts.size) {
                val x = (pts[i] * sx).toInt().coerceIn(0, w - 1)
                val y = (pts[i + 1] * sy).toInt().coerceIn(0, h - 1)
                // On ignore les 2 bits faibles de chaque couleur (bruit de compression)
                hsh = 31 * hsh + (buf.getInt(y * row + x * px) and 0xFCFCFCFC.toInt())
                i += 2
            }
            hsh
        } catch (e: Exception) {
            System.nanoTime()
        }
    }

    /** L'ancienne image peut encore etre en cours d'affichage : on la rend 2 images plus tard. */
    private fun releaseLater(img: Image, hb: HardwareBuffer?, bmp: Bitmap?) {
        val ch = Choreographer.getInstance()
        ch.postFrameCallback {
            ch.postFrameCallback {
                try { bmp?.recycle() } catch (e: Exception) { }
                try { hb?.close() } catch (e: Exception) { }
                try { img.close() } catch (e: Exception) { }
            }
        }
    }

    /** Ecran tourne : on adapte la taille de la copie de l'ecran. */
    fun reconfigure() {
        val d = display ?: return
        val old = reader
        val (dw, dh, dpi) = size()
        if (old != null && old.width == dw && old.height == dh) { force = true; return }
        val r = newReader(dw, dh)
        reader = r
        d.resize(dw, dh, dpi)
        d.surface = r.surface
        force = true
        // L'ancien lecteur (et ses images) est ferme une fois que l'ilot utilise les nouvelles images
        main.postDelayed({
            if (shown != null && old != null) {
                try { old.close() } catch (e: Exception) { }
            }
        }, 500)
    }

    fun pause() {
        paused = true
        display?.surface = null
    }

    fun resume() {
        paused = false
        force = true
        display?.surface = reader?.surface
    }

    fun stop() {
        try { projection?.stop() } catch (e: Exception) { }
        release()
    }

    private fun release() {
        try { display?.release() } catch (e: Exception) { }
        display = null
        projection = null
        val r = reader
        reader = null
        val img = shown
        val hb = shownHb
        val bmp = shownBmp
        shown = null
        shownHb = null
        shownBmp = null
        // On laisse le temps a l'ilot d'oublier la derniere image avant de tout fermer
        main.postDelayed({
            try { bmp?.recycle() } catch (e: Exception) { }
            try { hb?.close() } catch (e: Exception) { }
            try { img?.close() } catch (e: Exception) { }
            try { r?.close() } catch (e: Exception) { }
        }, 300)
    }

    companion object {
        private const val TAG = "LiquidIsland"
        /** Resolution de la copie de l'ecran (0.4 = 40 %) : largement suffisant pour un bord deforme. */
        const val SCALE = 0.4f
        private const val MAX_IMAGES = 5

        /** Resultat de l'autorisation de capture, garde le temps que le service le recupere. */
        @Volatile var pendingResult: Pair<Int, Intent>? = null
    }
}
