package com.liquidisland

import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapShader
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.PorterDuff
import android.graphics.PorterDuffColorFilter
import android.graphics.Shader
import android.graphics.Typeface
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.util.Log
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.Surface
import android.view.View
import android.view.WindowManager
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * Indicateur de volume facon iPhone, en verre liquide :
 * - une fine capsule verticale apparait a cote des boutons de volume ;
 * - le niveau se remplit en blanc et s'anime a chaque appui ;
 * - en butee (max ou min), la capsule s'etire puis revient en rebondissant ;
 * - au toucher elle s'elargit, « Volume » apparait au-dessus, et on regle le son au doigt.
 */
class VolumeHud(private val ctx: Context, private val main: Handler) {

    private val density = ctx.resources.displayMetrics.density
    private fun dp(v: Float) = v * density
    private val am = ctx.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    private var wm: WindowManager? = null
    private var root: View? = null
    private var view: HudView? = null
    private lateinit var lp: WindowManager.LayoutParams
    private var rotation = Surface.ROTATION_0
    var dark = true
    var clear = true

    private var stream = AudioManager.STREAM_MUSIC
    private val hideRunnable = Runnable { hide() }

    // Taille de la fenetre (portrait) : la capsule est collee au bord, la place restante sert a l'elargir
    private val winW get() = dp(96f).toInt()
    private val barH get() = dp(150f)
    private val labelH get() = dp(30f)
    private val winH get() = (barH + labelH + dp(40f)).toInt()

    val visible get() = (view?.shown ?: 0f) > 0.01f

    /** Cree la fenetre (via le service d'accessibilite, pour etre au-dessus de tout). */
    fun attach(host: Context, type: Int, rot: Int) {
        detach()
        rotation = rot
        val hostWm = host.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val v = HudView(ctx)
        view = v
        val r = when (rot) {
            Surface.ROTATION_90 -> Rotator(ctx, v, -90f).apply { portraitW = winW; portraitH = winH }
            Surface.ROTATION_270 -> Rotator(ctx, v, 90f).apply { portraitW = winW; portraitH = winH }
            else -> v
        }
        root = r
        lp = WindowManager.LayoutParams(
            0, 0, type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            if (Build.VERSION.SDK_INT >= 28) {
                layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            }
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        place()
        try {
            hostWm.addView(r, lp)
            wm = hostWm
        } catch (e: Exception) {
            Log.w(TAG, "Indicateur de volume impossible : $e")
            view = null
            root = null
        }
    }

    fun detach() {
        main.removeCallbacks(hideRunnable)
        root?.let { try { wm?.removeView(it) } catch (e: Exception) { } }
        root = null
        view = null
        wm = null
    }

    /** Place la fenetre contre le bord des boutons, en suivant la rotation de l'ecran. */
    private fun place() {
        val m = ctx.resources.displayMetrics
        // Taille de l'ecran en portrait
        val pw = min(m.widthPixels, m.heightPixels)
        val ph = max(m.widthPixels, m.heightPixels)
        val right = Prefs.volumeSide(ctx) != "left"
        val x0 = if (right) pw - winW else 0
        val y0 = (ph * Prefs.volumePos(ctx) / 100f - barH / 2f - labelH).toInt().coerceIn(0, ph - winH)
        when (rotation) {
            Surface.ROTATION_90 -> { lp.x = y0; lp.y = pw - (x0 + winW); lp.width = winH; lp.height = winW }
            Surface.ROTATION_270 -> { lp.x = ph - (y0 + winH); lp.y = x0; lp.width = winH; lp.height = winW }
            else -> { lp.x = x0; lp.y = y0; lp.width = winW; lp.height = winH }
        }
        view?.onRight = right
    }

    /** Reglages changes (cote, position) : on replace et on montre un apercu. */
    fun refresh() {
        val r = root ?: return
        place()
        try { wm?.updateViewLayout(r, lp) } catch (e: Exception) { }
        view?.invalidate()
    }

    private fun setTouchable(on: Boolean) {
        val r = root ?: return
        val flag = WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        val nf = if (on) lp.flags and flag.inv() else lp.flags or flag
        if (nf == lp.flags) return
        lp.flags = nf
        try { wm?.updateViewLayout(r, lp) } catch (e: Exception) { }
    }

    // ---------- Boutons de volume ----------

    /** Appele par l'accessibilite. Renvoie true si l'appui est gere (le panneau du systeme n'apparait pas). */
    fun onKey(e: KeyEvent): Boolean {
        if (view == null || !Prefs.volumeHud(ctx)) return false
        val up = e.keyCode == KeyEvent.KEYCODE_VOLUME_UP
        if (!up && e.keyCode != KeyEvent.KEYCODE_VOLUME_DOWN) return false
        // Appel en cours : on laisse le telephone gerer
        if (am.mode == AudioManager.MODE_IN_CALL || am.mode == AudioManager.MODE_IN_COMMUNICATION ||
            am.mode == AudioManager.MODE_RINGTONE
        ) return false
        if (e.action != KeyEvent.ACTION_DOWN) return true
        stream = AudioManager.STREAM_MUSIC
        val before = am.getStreamVolume(stream)
        val max = am.getStreamMaxVolume(stream)
        am.adjustStreamVolume(stream, if (up) AudioManager.ADJUST_RAISE else AudioManager.ADJUST_LOWER, 0)
        val after = am.getStreamVolume(stream)
        Log.i(TAG, "Volume : $after/$max")
        show()
        val v = view ?: return true
        v.animateLevel(after.toFloat() / max)
        // En butee : la capsule s'etire puis revient
        if (after == before && ((up && after == max) || (!up && after == 0))) v.bump(if (up) 1f else -1f)
        return true
    }

    fun show() {
        val v = view ?: return
        main.removeCallbacks(hideRunnable)
        if (v.shown < 1f && !v.showing) {
            // L'ancienne image du fond a pu etre liberee pendant que l'indicateur etait cache
            v.glass.backdrop = null
            v.level = am.getStreamVolume(stream).toFloat() / am.getStreamMaxVolume(stream)
            v.dark = dark
            v.clear = clear
            v.animateShown(1f)
        }
        setTouchable(true)
        main.postDelayed(hideRunnable, HIDE_MS)
    }

    private fun hide() {
        val v = view ?: return
        if (v.touching) { main.postDelayed(hideRunnable, HIDE_MS); return }
        v.animateShown(0f)
        setTouchable(false)
    }

    /** Apercu depuis les reglages */
    fun preview() = show()

    /** Fond capture pour l'effet goutte (meme image que l'ilot). */
    fun setBackdrop(bmp: Bitmap, sx: Float, sy: Float) {
        val v = view ?: return
        if (!visible) return
        val r = root ?: return
        if (!r.isAttachedToWindow) return
        val loc = IntArray(2)
        r.getLocationOnScreen(loc)
        val m = Matrix()
        if (r !== v) v.transformMatrixToGlobal(m)
        m.postTranslate(loc[0].toFloat(), loc[1].toFloat())
        m.postScale(sx, sy)
        val inv = Matrix()
        m.invert(inv)
        val sh = BitmapShader(bmp, Shader.TileMode.MIRROR, Shader.TileMode.MIRROR)
        sh.setLocalMatrix(inv)
        v.glass.backdrop = sh
        v.invalidate()
    }

    fun clearBackdrop() {
        view?.glass?.backdrop = null
        view?.invalidate()
    }

    // ---------- Dessin et toucher ----------

    @SuppressLint("ViewConstructor")
    private inner class HudView(c: Context) : View(c) {
        val glass = LiquidGlass(density).apply { tint = 0.15f }
        var onRight = true
        var dark = true
        var clear = true
        var shown = 0f
        var showing = false
        var level = 0.5f
        var widen = 0f     // 0 = fine capsule, 1 = elargie (toucher)
        var stretch = 0f   // etirement en butee (+ haut, - bas), en px
        var touching = false
        private var shownAnim: ValueAnimator? = null
        private var levelAnim: ValueAnimator? = null
        private var widenAnim: ValueAnimator? = null
        private var bumpAnim: ValueAnimator? = null
        private var lastIndex = -1

        private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
        private val label = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
            textSize = dp(13f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            textAlign = Paint.Align.CENTER
            setShadowLayer(dp(4f), 0f, dp(0.5f), Color.argb(170, 0, 0, 0))
        }
        private val iconHigh = c.getDrawable(R.drawable.sc_volume)!!.mutate()
        private val iconLow = c.getDrawable(R.drawable.sc_volume_low)!!.mutate()
        private val iconOff = c.getDrawable(R.drawable.sc_volume_off)!!.mutate()

        private fun anim(from: Float, to: Float, s: Spring, set: (Float) -> Unit) =
            ValueAnimator.ofFloat(from, to).apply {
                duration = s.durationMs
                interpolator = s
                addUpdateListener { set(it.animatedValue as Float); invalidate() }
            }

        fun animateShown(to: Float) {
            shownAnim?.cancel()
            showing = to > 0f
            shownAnim = anim(shown, to, if (to > 0f) Spring.OPEN else Spring.CLOSE) { shown = it }.also { it.start() }
        }

        fun animateLevel(to: Float) {
            levelAnim?.cancel()
            levelAnim = anim(level, to.coerceIn(0f, 1f), Spring.SOFT) { level = it }.also { it.start() }
        }

        private fun animateWiden(to: Float) {
            widenAnim?.cancel()
            widenAnim = anim(widen, to, Spring.OPEN) { widen = it }.also { it.start() }
        }

        fun bump(dir: Float) {
            bumpAnim?.cancel()
            performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
            // On tire d'un coup puis le ressort ramene la capsule (avec un petit rebond)
            bumpAnim = anim(dir * dp(14f), 0f, Spring.POP) { stretch = it }.also { it.start() }
        }

        /** Rectangle de la capsule pour l'etat actuel. */
        private fun bar(): FloatArray {
            val margin = dp(7f)
            val w = dp(11f) + (dp(46f) - dp(11f)) * widen
            // Entree : la capsule glisse depuis le bord
            val slide = (1f - shown) * dp(26f)
            val l: Float
            val r: Float
            if (onRight) { r = width - margin + slide; l = r - w } else { l = margin - slide; r = l + w }
            val bottom = labelH + barH + dp(20f) + min(0f, -stretch)
            val top = labelH + dp(20f) - max(0f, stretch)
            return floatArrayOf(l, top, r, bottom)
        }

        override fun onDraw(c: Canvas) {
            if (shown <= 0.01f) return
            val (l, t, r, b) = bar().let { arrayOf(it[0], it[1], it[2], it[3]) }
            val a = shown.coerceIn(0f, 1f)
            val save = c.saveLayerAlpha(0f, 0f, width.toFloat(), height.toFloat(), (a * 255).toInt())
            glass.dark = dark
            glass.clear = clear
            glass.build(l, t, r, b)
            glass.drawBody(c)

            // Niveau : blanc qui monte depuis le bas, coupe a la forme de la capsule
            c.save()
            c.clipPath(glass.path)
            val lt = b - (b - t) * level.coerceIn(0f, 1f)
            fill.shader = LinearGradient(0f, lt, 0f, b, Color.argb(245, 255, 255, 255), Color.argb(225, 236, 240, 248), Shader.TileMode.CLAMP)
            c.drawRect(l, lt, r, b, fill)

            // Icone du haut-parleur en bas, quand la capsule est assez large
            if (widen > 0.35f) {
                val ic = when {
                    level < 0.01f -> iconOff
                    level < 0.5f -> iconLow
                    else -> iconHigh
                }
                val s = dp(18f) * (0.6f + 0.4f * widen)
                val cx = (l + r) / 2f
                val cy = b - dp(18f)
                val onFill = cy > lt
                ic.colorFilter = PorterDuffColorFilter(
                    if (onFill) Color.argb(230, 40, 44, 56) else Color.WHITE, PorterDuff.Mode.SRC_IN
                )
                ic.alpha = ((widen - 0.35f) / 0.65f * 255).toInt().coerceIn(0, 255)
                ic.setBounds((cx - s / 2).toInt(), (cy - s / 2).toInt(), (cx + s / 2).toInt(), (cy + s / 2).toInt())
                ic.draw(c)
            }
            c.restore()
            glass.drawEdges(c)

            // « Volume » au-dessus, quand on touche
            if (widen > 0.05f) {
                label.alpha = (widen * 255).toInt().coerceIn(0, 255)
                c.drawText("Volume", (l + r) / 2f, t - dp(10f), label)
            }
            c.restoreToCount(save)
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            val (l, t, r, b) = bar().let { arrayOf(it[0], it[1], it[2], it[3]) }
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    // Zone de toucher genereuse autour de la fine capsule
                    if (e.x < l - dp(28f) || e.x > r + dp(28f) || e.y < t - dp(24f) || e.y > b + dp(24f)) return false
                    touching = true
                    main.removeCallbacks(hideRunnable)
                    animateWiden(1f)
                    lastIndex = am.getStreamVolume(stream)
                    setFromTouch(e.y, t, b)
                }
                MotionEvent.ACTION_MOVE -> if (touching) setFromTouch(e.y, t, b)
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    touching = false
                    animateWiden(0f)
                    if (stretch != 0f) {
                        bumpAnim?.cancel()
                        bumpAnim = anim(stretch, 0f, Spring.POP) { stretch = it }.also { it.start() }
                    }
                    main.removeCallbacks(hideRunnable)
                    main.postDelayed(hideRunnable, HIDE_MS)
                }
            }
            return true
        }

        private fun setFromTouch(y: Float, t: Float, b: Float) {
            val f = ((b - y) / (b - t)).coerceIn(0f, 1f)
            levelAnim?.cancel()
            level = f
            val max = am.getStreamMaxVolume(stream)
            val idx = (f * max).roundToInt()
            if (idx != lastIndex) {
                am.setStreamVolume(stream, idx, 0)
                if (idx == 0 || idx == max) performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                lastIndex = idx
            }
            // Tirer au-dela des bords : leger etirement elastique
            stretch = when {
                y < t -> min(dp(14f), (t - y) * 0.25f)
                y > b -> -min(dp(14f), (y - b) * 0.25f)
                else -> 0f
            }
            invalidate()
        }
    }

    companion object {
        private const val TAG = "LiquidIsland"
        private const val HIDE_MS = 1800L
    }
}
