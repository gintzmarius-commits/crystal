package com.liquidisland

import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.drawable.Drawable
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.SystemClock
import android.util.Log
import android.view.View
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin

/** Ce qui joue en ce moment (Spotify, YouTube, Deezer...). */
data class MediaNow(
    val title: String,
    val artist: String,
    val app: String,
    val art: Bitmap?,
    val playing: Boolean,
    val position: Long,
    val positionAt: Long,
    val speed: Float,
    val duration: Long,
    val controller: MediaController
) {
    /** Position estimee maintenant (elle avance pendant la lecture). */
    fun now(): Long {
        var p = position
        if (playing) p += ((SystemClock.elapsedRealtime() - positionAt) * speed).toLong()
        if (duration > 0) p = min(p, duration)
        return max(0, p)
    }
}

/** Une tache en cours avec une barre (telechargement, envoi, mise a jour d'appli...). */
data class ProgressNow(
    val key: String,
    val pkg: String,
    val title: String,
    val text: String,
    val progress: Int,
    val max: Int,
    val indeterminate: Boolean,
    val icon: Drawable?,
    val intent: PendingIntent?
) {
    val fraction: Float get() = if (indeterminate || max <= 0) -1f else (progress.toFloat() / max).coerceIn(0f, 1f)
}

/**
 * Suit les lecteurs multimedia du telephone (necessite l'acces aux notifications).
 * Prefere celui qui joue ; une musique en pause reste affichee 25 s.
 */
class MediaWatcher(private val ctx: Context, private val main: Handler, private val onChange: () -> Unit) {
    private val msm = ctx.getSystemService(MediaSessionManager::class.java)
    private val comp = ComponentName(ctx, NotifListener::class.java)
    private var controller: MediaController? = null
    private var started = false
    private var pausedSince = 0L

    private val callback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) { repick() }
        override fun onMetadataChanged(metadata: MediaMetadata?) = onChange()
        override fun onSessionDestroyed() { repick() }
    }

    private val sessions = MediaSessionManager.OnActiveSessionsChangedListener { list -> pick(list) }

    private val expire = Runnable { onChange() }

    fun start() {
        if (started) return
        try {
            msm.addOnActiveSessionsChangedListener(sessions, comp, main)
            started = true
            pick(msm.getActiveSessions(comp))
            Log.i(TAG, "Musique : lecteurs suivis")
        } catch (e: Exception) {
            // Acces aux notifications pas encore donne : on reessaiera quand il sera actif
            Log.i(TAG, "Musique : pas encore d'acces ($e)")
        }
    }

    fun stop() {
        try { msm.removeOnActiveSessionsChangedListener(sessions) } catch (e: Exception) { }
        controller?.unregisterCallback(callback)
        controller = null
        started = false
    }

    private fun repick() {
        try { pick(msm.getActiveSessions(comp)) } catch (e: Exception) { onChange() }
    }

    private fun pick(list: List<MediaController>?) {
        val all = list.orEmpty()
        val best = all.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
            ?: all.firstOrNull { it.packageName == controller?.packageName }
            ?: all.firstOrNull()
        if (best?.sessionToken != controller?.sessionToken) {
            controller?.unregisterCallback(callback)
            controller = best
            best?.registerCallback(callback, main)
        }
        onChange()
    }

    fun current(): MediaNow? {
        if (!Prefs.liveMedia(ctx)) return null
        val c = controller ?: return null
        val meta = c.metadata ?: return null
        val title = meta.getString(MediaMetadata.METADATA_KEY_TITLE)
            ?: meta.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE) ?: return null
        if (title.isBlank()) return null
        val st = c.playbackState
        val playing = st?.state == PlaybackState.STATE_PLAYING || st?.state == PlaybackState.STATE_BUFFERING
        val now = SystemClock.elapsedRealtime()
        if (playing) pausedSince = 0L else if (pausedSince == 0L) pausedSince = now
        main.removeCallbacks(expire)
        if (!playing) {
            val left = PAUSE_KEEP_MS - (now - pausedSince)
            if (left <= 0) return null
            main.postDelayed(expire, left + 50)
        }
        val artist = meta.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: meta.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: meta.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE) ?: ""
        val art = meta.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: meta.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: meta.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
        val app = try {
            ctx.packageManager.getApplicationLabel(ctx.packageManager.getApplicationInfo(c.packageName, 0)).toString()
        } catch (e: Exception) { c.packageName }
        return MediaNow(
            title, artist, app, art, playing,
            st?.position ?: 0L, st?.lastPositionUpdateTime ?: now, st?.playbackSpeed?.takeIf { it > 0f } ?: 1f,
            meta.getLong(MediaMetadata.METADATA_KEY_DURATION), c
        )
    }

    companion object {
        private const val TAG = "LiquidIsland"
        private const val PAUSE_KEEP_MS = 25_000L
    }
}

/** Petites barres qui dansent pendant la lecture (a droite de l'ilot replie). */
class EqualizerView(ctx: Context) : View(ctx) {
    var playing = false
        set(v) { field = v; invalidate() }
    var color = 0xFFFFFFFF.toInt()
        set(v) { field = v; paint.color = v; invalidate() }
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFFFFFFF.toInt() }
    private val heights = FloatArray(4) { 0.3f }
    private var t = 0f
    private val r = RectF()

    override fun onDraw(c: Canvas) {
        val bw = width / 7f
        for (i in 0 until 4) {
            val target = if (playing) 0.25f + 0.75f * abs(sin(t * (0.9f + i * 0.33f) + i * 1.7f)) else 0.25f
            heights[i] += (target - heights[i]) * 0.5f
            val h = max(bw, height * heights[i])
            val x = i * bw * 2f
            r.set(x, (height - h) / 2f, x + bw, (height + h) / 2f)
            c.drawRoundRect(r, bw / 2f, bw / 2f, paint)
        }
        if (playing && isShown) {
            t += 0.38f
            postInvalidateDelayed(70)
        }
    }
}

/** Anneau de progression (telechargement) ; tourne quand la taille totale est inconnue. */
class RingView(ctx: Context) : View(ctx) {
    var fraction = -1f
        set(v) { field = v; invalidate() }
    var color = 0xFFFFFFFF.toInt()
        set(v) { field = v; arc.color = v; track.color = (v and 0x00FFFFFF) or 0x50000000; invalidate() }
    private val d = ctx.resources.displayMetrics.density
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = 2.6f * d; color = 0x50FFFFFF }
    private val arc = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = 2.6f * d; strokeCap = Paint.Cap.ROUND; color = 0xFFFFFFFF.toInt() }
    private val r = RectF()
    private var spin = 0f

    override fun onDraw(c: Canvas) {
        val s = track.strokeWidth
        r.set(s, s, width - s, height - s)
        c.drawOval(r, track)
        if (fraction >= 0f) c.drawArc(r, -90f, max(4f, 360f * fraction), false, arc)
        else {
            c.drawArc(r, spin, 100f, false, arc)
            spin = (spin + 14f) % 360f
            if (isShown) postInvalidateDelayed(40)
        }
    }
}

/** Barre de progression fine (musique, telechargement) ; un segment glisse si la fin est inconnue. */
class BarView(ctx: Context) : View(ctx) {
    var fraction = 0f
        set(v) { field = v; invalidate() }
    var color = 0xFFFFFFFF.toInt()
        set(v) { field = v; fill.color = v; back.color = (v and 0x00FFFFFF) or 0x45000000; invalidate() }
    private val back = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x45FFFFFF }
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFFFFFFF.toInt() }
    private val r = RectF()
    private var slide = 0f

    override fun onDraw(c: Canvas) {
        val h = height.toFloat()
        r.set(0f, 0f, width.toFloat(), h)
        c.drawRoundRect(r, h / 2, h / 2, back)
        if (fraction >= 0f) {
            r.set(0f, 0f, max(h, width * fraction), h)
            c.drawRoundRect(r, h / 2, h / 2, fill)
        } else {
            val seg = width * 0.3f
            val x = (slide % (width + seg)) - seg
            r.set(max(0f, x), 0f, min(width.toFloat(), x + seg), h)
            if (r.width() > 0) c.drawRoundRect(r, h / 2, h / 2, fill)
            slide += width / 45f
            if (isShown) postInvalidateDelayed(30)
        }
    }
}
