package com.liquidisland

import android.animation.Animator
import android.animation.AnimatorListenerAdapter
import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Canvas
import android.graphics.PixelFormat
import android.graphics.RenderEffect
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.text.TextUtils
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.max
import kotlin.math.min

data class IslandNote(
    val title: String,
    val text: String,
    val icon: Drawable?,
    val intent: PendingIntent?
)

/** La vue de l'ilot : dessine le verre a la taille animee (pw x ph) et y decoupe le contenu. */
class IslandView(ctx: Context) : FrameLayout(ctx) {
    val glass = LiquidGlass(ctx.resources.displayMetrics.density)
    var pw = 0f
    var ph = 0f
    var onTap: (() -> Unit)? = null

    init {
        setWillNotDraw(false)
        clipChildren = false
    }

    override fun dispatchDraw(canvas: Canvas) {
        val l = (width - pw) / 2f
        glass.build(l, 0f, l + pw, ph)
        glass.drawBody(canvas)
        canvas.save()
        canvas.clipPath(glass.path)
        super.dispatchDraw(canvas)
        canvas.restore()
        glass.drawEdges(canvas)
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        if (e.action == MotionEvent.ACTION_UP && glass.rect.contains(e.x, e.y)) onTap?.invoke()
        return true
    }
}

class IslandService : Service() {

    private val main = Handler(Looper.getMainLooper())
    private lateinit var wm: WindowManager
    private var view: IslandView? = null
    private lateinit var lp: WindowManager.LayoutParams

    private lateinit var content: LinearLayout
    private lateinit var iconView: ImageView
    private lateinit var titleView: TextView
    private lateinit var textView: TextView

    private var expanded = false
    private var windowBig = false
    private var currentIntent: PendingIntent? = null
    private val running = mutableListOf<Animator>()
    private var blurAnim: ValueAnimator? = null
    private var gen = 0
    private val collapseRunnable = Runnable { collapse() }

    private val density get() = resources.displayMetrics.density
    private fun dp(v: Float) = (v * density).toInt()
    private fun dp(v: Int) = (v * density).toInt()

    // Tailles calculees depuis les reglages
    private var notch = false
    private var cw = 0
    private var ch = 0
    private var off = 0
    private var ew = 0
    private var eh = 0
    private var contentTop = 0
    private val side get() = dp(16)
    private val bottomPad get() = dp(10)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Îlot actif", NotificationManager.IMPORTANCE_MIN)
        )
        val notif = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_info_details)
            .setContentTitle("Liquid Island est actif")
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(1, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(1, notif)
        }
        if (!Settings.canDrawOverlays(this)) {
            stopSelf()
            return
        }
        wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        createIsland()
        instance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_SHOW && view != null) {
            val pkg = intent.getStringExtra(EXTRA_PKG) ?: packageName
            val icon = try { packageManager.getApplicationIcon(pkg) } catch (e: Exception) { null }
            @Suppress("DEPRECATION")
            val pi = intent.getParcelableExtra<PendingIntent>(EXTRA_PI)
            show(IslandNote(intent.getStringExtra(EXTRA_TITLE).orEmpty(), intent.getStringExtra(EXTRA_TEXT).orEmpty(), icon, pi))
        }
        return START_STICKY
    }

    // ---------- Construction ----------

    private fun readSettings() {
        notch = Prefs.notch(this)
        cw = dp(Prefs.width(this))
        ch = dp(Prefs.height(this))
        off = if (notch) 0 else dp(Prefs.offset(this))
        val screenW = resources.displayMetrics.widthPixels
        ew = max(cw, min(dp(360), screenW - dp(20)))
        val body = dp(64)
        if (notch) {
            eh = ch + body
            contentTop = ch
        } else {
            eh = max(dp(76), ch + dp(10))
            contentTop = (eh - body) / 2
        }
    }

    private fun createIsland() {
        readSettings()
        iconView = ImageView(this).apply {
            layoutParams = LinearLayout.LayoutParams(dp(36), dp(36))
        }
        titleView = TextView(this).apply {
            setTextColor(Glass.INK)
            textSize = 14f
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }
        textView = TextView(this).apply {
            setTextColor(Glass.INK_SOFT)
            textSize = 13f
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(titleView)
            addView(textView)
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginStart = dp(12)
            }
        }
        content = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(20), 0, dp(20), 0)
            addView(iconView)
            addView(column)
            alpha = 0f
            visibility = View.INVISIBLE
        }
        val v = IslandView(this)
        v.addView(content, contentParams())
        v.pw = cw.toFloat()
        v.ph = ch.toFloat()
        v.glass.notch = notch
        v.onTap = { onTap() }
        view = v

        lp = WindowManager.LayoutParams(
            0, 0,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            if (Build.VERSION.SDK_INT >= 28) {
                layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            }
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        applyWindow(big = false, update = false)
        wm.addView(v, lp)
        Log.i(TAG, "Ilot affiche (forme=${if (notch) "encoche" else "ile"})")
    }

    private fun contentParams() =
        FrameLayout.LayoutParams(ew, dp(64), Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = contentTop }

    /** Petite fenetre quand l'ilot est replie (pour ne pas bloquer l'ecran), grande pendant l'animation. */
    private fun applyWindow(big: Boolean, update: Boolean = true) {
        windowBig = big
        lp.y = off
        if (big) {
            lp.width = ew + 2 * side
            lp.height = eh + bottomPad
        } else {
            lp.width = cw + 2 * side
            lp.height = ch + dp(6)
        }
        if (update) view?.let { try { wm.updateViewLayout(it, lp) } catch (e: Exception) { } }
    }

    /** Applique les nouveaux reglages (taille, forme, position) sans redemarrer. */
    fun applySettings() {
        val v = view ?: return
        stopAnims()
        main.removeCallbacks(collapseRunnable)
        readSettings()
        expanded = false
        currentIntent = null
        content.alpha = 0f
        content.visibility = View.INVISIBLE
        content.layoutParams = contentParams()
        v.glass.notch = notch
        v.glass.tint = 0f
        v.pw = cw.toFloat()
        v.ph = ch.toFloat()
        applyWindow(big = false)
        v.invalidate()
    }

    // ---------- Animations ----------

    private fun stopAnims() {
        gen++
        running.toList().forEach { it.cancel() }
        running.clear()
        blurAnim?.cancel()
    }

    private fun spring(from: Float, to: Float, s: Spring, delay: Long = 0, set: (Float) -> Unit): ValueAnimator =
        ValueAnimator.ofFloat(from, to).apply {
            duration = s.durationMs
            startDelay = delay
            interpolator = s
            addUpdateListener { set(it.animatedValue as Float); view?.invalidate() }
        }

    private fun animateIsland(w: Float, h: Float, tint: Float, opening: Boolean, onDone: (() -> Unit)? = null) {
        val v = view ?: return
        stopAnims()
        val myGen = gen
        val anims = listOf(
            spring(v.pw, w, if (opening) Spring.OPEN_W else Spring.CLOSE) { v.pw = it },
            spring(v.ph, h, if (opening) Spring.OPEN else Spring.CLOSE, if (opening) 30 else 0) { v.ph = it },
            spring(v.glass.tint, tint, Spring.SOFT) { v.glass.tint = it }
        )
        var left = anims.size
        for (a in anims) {
            a.addListener(object : AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: Animator) {
                    running.remove(animation)
                    left--
                    if (left == 0 && myGen == gen) onDone?.invoke()
                }
            })
            running.add(a)
            a.start()
        }
    }

    private fun onTap() {
        if (!expanded) return
        currentIntent?.let { try { it.send() } catch (e: Exception) { Log.w(TAG, "Ouverture impossible", e) } }
        main.removeCallbacks(collapseRunnable)
        collapse()
    }

    fun show(note: IslandNote) {
        val v = view ?: return
        main.removeCallbacks(collapseRunnable)
        iconView.setImageDrawable(note.icon)
        titleView.text = note.title
        titleView.visibility = if (note.title.isEmpty()) View.GONE else View.VISIBLE
        textView.text = note.text
        textView.visibility = if (note.text.isEmpty()) View.GONE else View.VISIBLE
        currentIntent = note.intent

        if (!windowBig) applyWindow(big = true)
        if (expanded) {
            // Nouvelle notif alors que l'ilot est deja ouvert : petit rebond
            v.pw = ew * 0.94f
            v.ph = eh * 0.9f
        }
        expanded = true
        animateIsland(ew.toFloat(), eh.toFloat(), 1f, opening = true)

        // Le contenu arrive en douceur : fondu, leger zoom, net apres un leger flou, icone qui saute
        content.visibility = View.VISIBLE
        content.animate().cancel()
        content.alpha = 0f
        content.scaleX = 0.9f
        content.scaleY = 0.9f
        content.translationY = if (notch) -dp(6).toFloat() else 0f
        val soft = Spring.SOFT
        content.animate().alpha(1f).scaleX(1f).scaleY(1f).translationY(0f)
            .setStartDelay(110).setDuration(soft.durationMs).setInterpolator(soft).start()
        iconView.animate().cancel()
        iconView.scaleX = 0.3f
        iconView.scaleY = 0.3f
        val pop = Spring.POP
        iconView.animate().scaleX(1f).scaleY(1f).setStartDelay(150).setDuration(pop.durationMs).setInterpolator(pop).start()
        if (Build.VERSION.SDK_INT >= 31) {
            blurAnim?.cancel()
            blurAnim = ValueAnimator.ofFloat(dp(10).toFloat(), 0f).apply {
                startDelay = 110
                duration = 320
                addUpdateListener {
                    val r = it.animatedValue as Float
                    content.setRenderEffect(if (r < 0.5f) null else RenderEffect.createBlurEffect(r, r, Shader.TileMode.DECAL))
                }
                start()
            }
        }
        main.postDelayed(collapseRunnable, 5000)
        Log.i(TAG, "Notification affichee : ${note.title}")
    }

    private fun collapse() {
        currentIntent = null
        expanded = false
        content.animate().cancel()
        content.animate().alpha(0f).scaleX(0.92f).scaleY(0.92f).setStartDelay(0).setDuration(140)
            .setInterpolator(null)
            .withEndAction { content.visibility = View.INVISIBLE }.start()
        animateIsland(cw.toFloat(), ch.toFloat(), 0f, opening = false) {
            if (!expanded) applyWindow(big = false)
        }
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        main.removeCallbacksAndMessages(null)
        stopAnims()
        view?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        view = null
        super.onDestroy()
    }

    companion object {
        private const val TAG = "LiquidIsland"
        private const val CHANNEL = "island"
        const val ACTION_SHOW = "com.liquidisland.SHOW"
        const val EXTRA_TITLE = "title"
        const val EXTRA_TEXT = "text"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_PI = "pi"

        @Volatile
        var instance: IslandService? = null
            private set

        /** Affiche une notification dans l'ilot, en demarrant l'ilot si besoin. */
        fun deliver(ctx: Context, note: IslandNote, pkg: String) {
            val s = instance
            if (s != null) {
                s.main.post { s.show(note) }
                return
            }
            if (!Prefs.enabled(ctx) || !Settings.canDrawOverlays(ctx)) return
            val i = Intent(ctx, IslandService::class.java)
                .setAction(ACTION_SHOW)
                .putExtra(EXTRA_TITLE, note.title)
                .putExtra(EXTRA_TEXT, note.text)
                .putExtra(EXTRA_PKG, pkg)
                .putExtra(EXTRA_PI, note.intent)
            try {
                ctx.startForegroundService(i)
            } catch (e: Exception) {
                Log.w(TAG, "Demarrage de l'ilot refuse", e)
            }
        }
    }
}
