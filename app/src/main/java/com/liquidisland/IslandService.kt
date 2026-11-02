package com.liquidisland

import android.animation.Animator
import android.animation.AnimatorListenerAdapter
import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.app.ActivityOptions
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.RenderEffect
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.text.TextUtils
import android.util.Log
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

data class IslandNote(
    val title: String,
    val text: String,
    val icon: Drawable?,
    val intent: PendingIntent?,
    val pkg: String = "",
    val key: String? = null,
    val autoCancel: Boolean = false
)

/** Gestes reconnus sur l'ilot. */
interface IslandGestures {
    fun press(down: Boolean)
    fun tap()
    fun longPress()
    fun drag(dy: Float)
    fun release(dy: Float)
    fun outside()
}

/** Reconnait toucher, appui long et glissement vertical, pour l'ilot et pour la zone tactile. */
class GestureTracker(
    private val host: View,
    private val gestures: () -> IslandGestures?,
    private val inside: (Float, Float) -> Boolean
) {
    var longPressMs = 700L
    private val slop = ViewConfiguration.get(host.context).scaledTouchSlop
    private var downY = 0f
    private var lastDy = 0f
    var tracking = false
        private set
    var moved = false
        private set
    var longFired = false
        private set
    private val longRunnable = Runnable {
        longFired = true
        host.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
        gestures()?.longPress()
    }

    /** [allowTap] = false quand l'evenement passe d'abord par un parent (un bouton du menu peut le prendre). */
    fun handle(e: MotionEvent, allowTap: Boolean) {
        val g = gestures()
        when (e.actionMasked) {
            MotionEvent.ACTION_OUTSIDE -> g?.outside()
            MotionEvent.ACTION_DOWN -> {
                if (!inside(e.x, e.y)) { g?.outside(); return }
                tracking = true
                moved = false
                longFired = false
                downY = e.y
                lastDy = 0f
                g?.press(true)
                host.postDelayed(longRunnable, longPressMs)
            }
            MotionEvent.ACTION_MOVE -> {
                if (!tracking) return
                val dy = e.y - downY
                lastDy = dy
                if (!moved && abs(dy) > slop) {
                    moved = true
                    host.removeCallbacks(longRunnable)
                    if (!longFired) g?.press(false)
                }
                if (moved && !longFired) g?.drag(dy)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (!tracking) return
                tracking = false
                host.removeCallbacks(longRunnable)
                if (longFired) return
                if (moved) {
                    // Android peut interrompre (CANCEL) un glissement parti du haut de l'ecran :
                    // on garde alors la distance deja parcourue pour reconnaitre le geste.
                    g?.release(if (e.actionMasked == MotionEvent.ACTION_UP) e.y - downY else lastDy)
                } else {
                    g?.press(false)
                    if (allowTap && e.actionMasked == MotionEvent.ACTION_UP && inside(e.x, e.y)) g?.tap()
                }
            }
        }
    }
}

/** La vue de l'ilot : dessine le verre a la taille animee (pw x ph), y decoupe le contenu et lit les gestes. */
class IslandView(ctx: Context) : FrameLayout(ctx) {
    val glass = LiquidGlass(ctx.resources.displayMetrics.density)
    var pw = 0f
    var ph = 0f
    var gestures: IslandGestures? = null
    val tracker = GestureTracker(this, { gestures }, { x, y -> glass.rect.contains(x, y) })
    /** Appele a chaque image avec la position et la taille de la forme (pour le flou derriere). */
    var onShape: ((left: Float, w: Float, h: Float) -> Unit)? = null

    init {
        setWillNotDraw(false)
        clipChildren = false
    }

    override fun dispatchDraw(canvas: Canvas) {
        val l = (width - pw) / 2f
        onShape?.invoke(l, pw, ph)
        glass.build(l, 0f, l + pw, ph)
        glass.drawBody(canvas)
        canvas.save()
        canvas.clipPath(glass.path)
        super.dispatchDraw(canvas)
        canvas.restore()
        glass.drawEdges(canvas)
    }

    /** Les gestes passent avant les enfants, sauf un simple appui (laisse aux boutons du menu). */
    override fun onInterceptTouchEvent(e: MotionEvent): Boolean {
        tracker.handle(e, allowTap = false)
        return tracker.moved || tracker.longFired
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        if (e.actionMasked != MotionEvent.ACTION_DOWN || !tracker.tracking) tracker.handle(e, allowTap = true)
        return true
    }
}

/** Petite zone tactile invisible posee sur l'ilot replie (la grande fenetre, elle, laisse passer les touchers). */
class TouchPad(ctx: Context, private val gestures: IslandGestures) : View(ctx) {
    private val tracker = GestureTracker(this, { gestures }, { _, _ -> true })

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        tracker.handle(e, allowTap = true)
        return true
    }
}

class IslandService : Service(), IslandGestures {

    private enum class Mode { COLLAPSED, NOTIF, MENU }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var wm: WindowManager
    private var view: IslandView? = null
    private lateinit var lp: WindowManager.LayoutParams
    private var pad: TouchPad? = null
    private var blurDialog: android.app.Dialog? = null
    private var blurKey = ""
    private var blurUnavailable = false
    private var dark = true
    private lateinit var lpPad: WindowManager.LayoutParams

    private lateinit var content: LinearLayout
    private lateinit var iconView: ImageView
    private lateinit var titleView: TextView
    private lateinit var textView: TextView
    private lateinit var menu: LinearLayout

    private var mode = Mode.COLLAPSED
    private var aboveStatusBar = false
    private var windowBig = false
    private var note: IslandNote? = null
    private val running = mutableListOf<Animator>()
    private var blurAnim: ValueAnimator? = null
    private var gen = 0
    private val collapseRunnable = Runnable { collapse("minuteur") }

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
    private var mh = 0
    private var contentTop = 0
    private var menuTop = 0
    private val side get() = dp(16)
    private val bottomPad get() = dp(28)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        // Le telephone passe en mode sombre/clair : la teinte « auto » suit
        main.post { applySettings() }
    }

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Îlot actif", NotificationManager.IMPORTANCE_MIN)
        )
        val notif = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_info_details)
            .setContentTitle("Wave Island est actif")
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(1, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(1, notif)
        }
        if (!Settings.canDrawOverlays(this) && IslandAccessibility.instance == null) {
            stopSelf()
            return
        }
        createIsland()
        instance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_SHOW && view != null) {
            val pkg = intent.getStringExtra(EXTRA_PKG) ?: packageName
            val icon = try { packageManager.getApplicationIcon(pkg) } catch (e: Exception) { null }
            @Suppress("DEPRECATION")
            val pi = intent.getParcelableExtra<PendingIntent>(EXTRA_PI)
            show(IslandNote(intent.getStringExtra(EXTRA_TITLE).orEmpty(), intent.getStringExtra(EXTRA_TEXT).orEmpty(), icon, pi, pkg))
        }
        return START_STICKY
    }

    // ---------- Construction ----------

    private fun ink() = if (dark) Color.WHITE else Glass.ISLAND_INK
    private fun inkSoft() = if (dark) Color.argb(185, 255, 255, 255) else Glass.ISLAND_INK_SOFT

    private fun readSettings() {
        dark = Prefs.isDark(this)
        notch = Prefs.notch(this)
        cw = dp(Prefs.width(this))
        ch = dp(Prefs.height(this))
        off = if (notch) 0 else dp(Prefs.offset(this))
        val screenW = resources.displayMetrics.widthPixels
        ew = max(cw, min(dp(360), screenW - dp(20)))
        // Ouvert, l'ilot descend sous la barre d'etat pour que l'heure et la batterie ne cachent pas le texte
        val sb = statusBarHeight()
        val body = dp(64)
        if (notch) {
            contentTop = max(ch, sb) + dp(2)
            menuTop = max(ch, sb)
        } else {
            contentTop = max(dp(6), sb - off + dp(2))
            menuTop = max(0, sb - off)
        }
        eh = contentTop + body + dp(8)
        val rows = max(1, (Shortcuts.load(this).size + 3) / 4)
        mh = menuTop + dp(14) * 2 + rows * dp(78)
    }

    private fun createIsland() {
        readSettings()
        iconView = ImageView(this).apply {
            layoutParams = LinearLayout.LayoutParams(dp(36), dp(36))
        }
        titleView = TextView(this).apply {
            setTextColor(ink())
            textSize = 14f
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }
        textView = TextView(this).apply {
            setTextColor(inkSoft())
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
        menu = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
        }
        val v = IslandView(this)
        v.addView(content, contentParams())
        v.addView(menu, menuParams())
        v.pw = cw.toFloat()
        v.ph = ch.toFloat()
        v.glass.notch = notch
        v.glass.dark = dark
        v.gestures = this
        v.onShape = { l, w, h -> moveBlur(l, w, h) }
        view = v

        // Avec l'accessibilite, l'ilot passe au-dessus de la barre d'etat et recoit les touchers
        val acc = IslandAccessibility.instance
        aboveStatusBar = acc != null
        wm = (acc ?: this).getSystemService(Context.WINDOW_SERVICE) as WindowManager
        lp = WindowManager.LayoutParams(
            0, 0,
            if (acc != null) WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY
            else WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or
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
        lpPad = WindowManager.LayoutParams().apply {
            copyFrom(lp)
            flags = lp.flags and WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH.inv()
        }
        applyWindow(big = false, update = false)
        createBlur(acc ?: this, lp.type)
        v.glass.blurred = blurDialog != null
        wm.addView(v, lp)
        val p = TouchPad(this, this)
        pad = p
        wm.addView(p, lpPad)
        Log.i(TAG, "Ilot affiche (forme=${if (notch) "encoche" else "ile"}, au-dessus de la barre d'etat=$aboveStatusBar)")
    }

    private fun contentParams() =
        FrameLayout.LayoutParams(ew, dp(64), Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = contentTop }

    private fun menuParams() =
        FrameLayout.LayoutParams(ew, mh - menuTop, Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply {
            topMargin = menuTop
        }

    // ---------- Flou limite a la forme de l'ilot ----------

    /**
     * Une petite fenetre placee SOUS l'ilot, exactement a sa taille, qui floute ce qu'il y a derriere.
     * Elle suit la forme a chaque image ; le verre (fenetre du dessus) ne bouge jamais, donc pas de saut.
     */
    private fun createBlur(host: Context, type: Int) {
        if (Build.VERSION.SDK_INT < 31 || !Prefs.blur(this) || blurDialog != null) return
        val hostWm = host.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        if (!hostWm.isCrossWindowBlurEnabled) {
            blurUnavailable = true
            Log.i(TAG, "Flou indisponible sur ce telephone (ou economie d'energie)")
            return
        }
        try {
            val d = android.app.Dialog(host, android.R.style.Theme_Panel)
            d.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE)
            d.setContentView(View(host))
            d.setCancelable(false)
            val w = d.window!!
            w.setType(type)
            w.clearFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            w.addFlags(
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                    WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                    WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                    WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
            )
            w.setWindowAnimations(0)
            w.setGravity(Gravity.TOP or Gravity.START)
            w.setDecorFitsSystemWindows(false)
            val a = w.attributes
            a.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            a.fitInsetsTypes = 0
            a.width = cw
            a.height = ch
            a.x = (resources.displayMetrics.widthPixels - cw) / 2
            a.y = off
            w.attributes = a
            w.setBackgroundBlurRadius(dp(24))
            w.setBackgroundDrawable(blurShape(cw, ch))
            d.show()
            w.decorView.setPadding(0, 0, 0, 0)
            blurDialog = d
            blurKey = ""
            Log.i(TAG, "Flou du fond actif")
        } catch (e: Exception) {
            Log.w(TAG, "Flou impossible : $e")
            blurDialog = null
        }
    }

    private fun blurShape(w: Int, h: Int): GradientDrawable =
        GradientDrawable().apply {
            setColor(Color.argb(1, 0, 0, 0))
            cornerRadius = if (notch) min(min(h * 0.5f, dp(28).toFloat()), w / 2f) else min(w, h) / 2f
            setBounds(0, 0, w, h)
        }

    private fun moveBlur(l: Float, w: Float, h: Float) {
        val d = blurDialog ?: return
        val win = d.window ?: return
        val screenX = (resources.displayMetrics.widthPixels - lp.width) / 2 + l.toInt()
        val iw = w.toInt()
        val ih = h.toInt()
        val key = "$screenX/$iw/$ih/$off"
        if (key == blurKey || iw <= 0 || ih <= 0) return
        blurKey = key
        val a = win.attributes
        a.x = screenX
        a.y = off
        a.width = iw
        a.height = ih
        win.attributes = a
        // Nouveau fond a chaque taille : le flou reprend l'arrondi de la forme
        win.setBackgroundDrawable(blurShape(iw, ih))
    }

    private fun removeBlur() {
        try { blurDialog?.dismiss() } catch (e: Exception) { }
        blurDialog = null
    }

    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dp(28)
    }

    /**
     * La fenetre d'affichage garde toujours la meme taille (la redimensionner faisait sauter l'ilot sur Xiaomi).
     * Replie : elle laisse passer les touchers et une petite zone tactile invisible prend le relais sur l'ilot.
     * Ouvert : elle recoit les touchers et la zone tactile est desactivee.
     */
    private fun applyWindow(big: Boolean, update: Boolean = true) {
        windowBig = big
        val noTouch = WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        lp.y = off
        lp.width = ew + 2 * side
        lp.height = max(eh, mh) + bottomPad
        lp.flags = if (big) lp.flags and noTouch.inv() else lp.flags or noTouch
        lpPad.y = off
        lpPad.width = cw + 2 * dp(6)
        lpPad.height = ch + dp(8)
        lpPad.flags = if (big) lpPad.flags or noTouch else lpPad.flags and noTouch.inv()
        if (update) {
            view?.let { try { wm.updateViewLayout(it, lp) } catch (e: Exception) { } }
            pad?.let { try { wm.updateViewLayout(it, lpPad) } catch (e: Exception) { } }
        }
    }

    /** Recree la fenetre (quand l'accessibilite s'active ou se coupe). */
    fun rehost() {
        main.post {
            stopAnims()
            main.removeCallbacks(collapseRunnable)
            view?.let { try { wm.removeView(it) } catch (e: Exception) { } }
            pad?.let { try { wm.removeView(it) } catch (e: Exception) { } }
            removeBlur()
            view = null
            pad = null
            mode = Mode.COLLAPSED
            if (!Settings.canDrawOverlays(this) && IslandAccessibility.instance == null) {
                stopSelf()
                return@post
            }
            createIsland()
        }
    }

    /** Applique les nouveaux reglages (taille, forme, position, raccourcis) sans redemarrer. */
    fun applySettings() {
        val v = view ?: return
        stopAnims()
        main.removeCallbacks(collapseRunnable)
        readSettings()
        mode = Mode.COLLAPSED
        note = null
        content.alpha = 0f
        content.visibility = View.INVISIBLE
        content.layoutParams = contentParams()
        menu.visibility = View.GONE
        menu.layoutParams = menuParams()
        v.glass.notch = notch
        v.glass.dark = dark
        v.glass.tint = 0f
        titleView.setTextColor(ink())
        textView.setTextColor(inkSoft())
        val wantBlur = Prefs.blur(this) && Build.VERSION.SDK_INT >= 31
        // La fenetre de flou doit etre SOUS l'ilot : on recree tout dans le bon ordre
        if (wantBlur && blurDialog == null && !blurUnavailable) { rehost(); return }
        if (!wantBlur) removeBlur()
        v.glass.blurred = blurDialog != null
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

    private fun appear(target: View, offsetY: Float) {
        target.visibility = View.VISIBLE
        target.animate().cancel()
        target.alpha = 0f
        target.scaleX = 0.9f
        target.scaleY = 0.9f
        target.translationY = offsetY
        val soft = Spring.SOFT
        target.animate().alpha(1f).scaleX(1f).scaleY(1f).translationY(0f)
            .setStartDelay(110).setDuration(soft.durationMs).setInterpolator(soft).start()
        if (Build.VERSION.SDK_INT >= 31) {
            blurAnim?.cancel()
            blurAnim = ValueAnimator.ofFloat(dp(10).toFloat(), 0f).apply {
                startDelay = 110
                duration = 320
                addUpdateListener {
                    val r = it.animatedValue as Float
                    target.setRenderEffect(if (r < 0.5f) null else RenderEffect.createBlurEffect(r, r, Shader.TileMode.DECAL))
                }
                start()
            }
        }
    }

    private fun disappear(target: View, gone: Boolean = false) {
        target.animate().cancel()
        target.animate().alpha(0f).scaleX(0.92f).scaleY(0.92f).setStartDelay(0).setDuration(140)
            .setInterpolator(null)
            .withEndAction { target.visibility = if (gone) View.GONE else View.INVISIBLE }.start()
    }

    // ---------- Notification ----------

    fun show(n: IslandNote) {
        val v = view ?: return
        main.removeCallbacks(collapseRunnable)
        iconView.setImageDrawable(n.icon)
        titleView.text = n.title
        titleView.visibility = if (n.title.isEmpty()) View.GONE else View.VISIBLE
        textView.text = n.text
        textView.visibility = if (n.text.isEmpty()) View.GONE else View.VISIBLE
        note = n

        if (!windowBig) applyWindow(big = true)
        if (mode == Mode.MENU) disappear(menu, gone = true)
        if (mode == Mode.NOTIF) {
            // Nouvelle notif alors que l'ilot est deja ouvert : petit rebond
            v.pw = ew * 0.94f
            v.ph = eh * 0.9f
        }
        mode = Mode.NOTIF
        animateIsland(ew.toFloat(), eh.toFloat(), 1f, opening = true)
        appear(content, if (notch) -dp(6).toFloat() else 0f)
        iconView.animate().cancel()
        iconView.scaleX = 0.3f
        iconView.scaleY = 0.3f
        val pop = Spring.POP
        iconView.animate().scaleX(1f).scaleY(1f).setStartDelay(150).setDuration(pop.durationMs).setInterpolator(pop).start()
        main.postDelayed(collapseRunnable, 6000)
        Log.i(TAG, "Notification affichee : ${n.title}")
    }

    private var opening = false

    /**
     * Ouvre l'appli de la notification : l'ilot s'etire vers le bas, puis l'appli s'ouvre
     * en grandissant depuis l'ilot jusqu'au plein ecran.
     */
    private fun openNote(floating: Boolean) {
        val n = note ?: return
        val v = view ?: return
        if (opening) return
        opening = true
        main.removeCallbacks(collapseRunnable)
        stopAnims()
        content.animate().cancel()
        content.animate().alpha(0f).setDuration(90).setStartDelay(0).start()
        val w0 = v.pw
        val h0 = v.ph
        val w1 = ew + dp(6).toFloat()
        val h1 = min((eh + dp(40)).toFloat(), (max(eh, mh) + bottomPad - dp(2)).toFloat())
        val stretch = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 150
            interpolator = android.view.animation.DecelerateInterpolator(1.5f)
            addUpdateListener {
                val f = it.animatedValue as Float
                v.pw = w0 + (w1 - w0) * f
                v.ph = h0 + (h1 - h0) * f
                v.invalidate()
            }
            addListener(object : AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: Animator) {
                    launchNote(n, floating)
                    // L'ilot reprend sa place une fois l'appli lancee
                    main.postDelayed({
                        opening = false
                        collapse("ouverture")
                    }, 220)
                }
            })
        }
        running.add(stretch)
        stretch.start()
    }

    private fun launchNote(n: IslandNote, floating: Boolean) {
        val v = view
        // L'appli s'ouvre en grandissant depuis la forme actuelle de l'ilot
        val opts = if (v != null && v.isAttachedToWindow) {
            val l = ((v.width - v.pw) / 2f).toInt()
            ActivityOptions.makeClipRevealAnimation(v, l, 0, v.pw.toInt(), v.ph.toInt())
        } else {
            ActivityOptions.makeBasic()
        }
        // Taille de fenetre seulement si le telephone gere les fenetres libres (sinon Android peut mal le prendre)
        val canFloat = packageManager.hasSystemFeature(android.content.pm.PackageManager.FEATURE_FREEFORM_WINDOW_MANAGEMENT)
        if (floating && canFloat) {
            val sw = resources.displayMetrics.widthPixels
            val sh = resources.displayMetrics.heightPixels
            val w = (sw * 0.86f).toInt()
            val h = (sh * 0.55f).toInt()
            val top = dp(90)
            opts.setLaunchBounds(Rect((sw - w) / 2, top, (sw + w) / 2, top + h))
        }
        if (Build.VERSION.SDK_INT >= 34) {
            @Suppress("DEPRECATION")
            opts.setPendingIntentBackgroundActivityStartMode(ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
        }
        var ok = false
        n.intent?.let {
            try {
                it.send(this, 0, null, null, null, null, opts.toBundle())
                ok = true
            } catch (e: Exception) {
                Log.w(TAG, "Intent de la notif refuse", e)
            }
        }
        if (!ok && n.pkg.isNotEmpty()) {
            packageManager.getLaunchIntentForPackage(n.pkg)?.let {
                it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                try {
                    startActivity(it, opts.toBundle())
                    ok = true
                } catch (e: Exception) {
                    Log.w(TAG, "Ouverture de l'appli impossible", e)
                }
            }
        }
        Log.i(TAG, "Ouverture ${n.pkg} (flottante=$floating, ok=$ok, fenetres libres=$canFloat)")
        if (ok && n.autoCancel && n.key != null) NotifListener.cancel(n.key)
    }

    // ---------- Menu ----------

    private fun buildMenu() {
        menu.removeAllViews()
        val items = Shortcuts.load(this).mapNotNull { Shortcuts.resolve(this, it) }
        if (items.isEmpty()) {
            menu.addView(TextView(this).apply {
                text = "Ajoute des raccourcis dans Wave Island"
                setTextColor(inkSoft())
                textSize = 13f
                gravity = Gravity.CENTER
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            return
        }
        menu.setPadding(dp(10), dp(14), dp(10), dp(14))
        var row: LinearLayout? = null
        items.forEachIndexed { i, item ->
            if (i % 4 == 0) {
                row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
                menu.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(78)))
            }
            row!!.addView(menuItem(item, i), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        }
        // Complete la derniere ligne pour garder l'alignement
        val rest = (4 - items.size % 4) % 4
        repeat(rest) { row!!.addView(View(this), LinearLayout.LayoutParams(0, 1, 1f)) }
    }

    private fun menuItem(item: Shortcuts.Item, index: Int): View {
        val circle = FrameLayout(this).apply {
            // Bulle de verre pour chaque raccourci
            background = GradientDrawable(
                GradientDrawable.Orientation.TOP_BOTTOM,
                if (dark) intArrayOf(Color.argb(60, 255, 255, 255), Color.argb(22, 255, 255, 255))
                else intArrayOf(Color.argb(200, 255, 255, 255), Color.argb(140, 255, 255, 255))
            ).apply {
                shape = GradientDrawable.OVAL
                setStroke(max(1, dp(1)), if (dark) Color.argb(80, 255, 255, 255) else Color.argb(230, 255, 255, 255))
            }
            addView(ImageView(this@IslandService).apply {
                setImageDrawable(item.icon)
                if (item.tintIcon) imageTintList = android.content.res.ColorStateList.valueOf(ink())
            }, FrameLayout.LayoutParams(
                if (item.tintIcon) dp(24) else dp(40),
                if (item.tintIcon) dp(24) else dp(40),
                Gravity.CENTER
            ))
        }
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            addView(circle, LinearLayout.LayoutParams(dp(50), dp(50)))
            addView(TextView(this@IslandService).apply {
                text = item.label
                textSize = 11f
                setTextColor(ink())
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                gravity = Gravity.CENTER
                setPadding(dp(2), dp(4), dp(2), 0)
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
            isClickable = true
            setOnClickListener {
                it.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                // petit « appui » visuel puis lancement
                circle.animate().scaleX(0.85f).scaleY(0.85f).setDuration(80).withEndAction {
                    circle.animate().scaleX(1f).scaleY(1f).setDuration(Spring.POP.durationMs).setInterpolator(Spring.POP).start()
                }.start()
                main.postDelayed({
                    Shortcuts.run(this@IslandService, item.id)
                    collapse("raccourci")
                }, 140)
            }
        }
        // Apparition en cascade, chaque icone rebondit
        circle.scaleX = 0.3f
        circle.scaleY = 0.3f
        box.alpha = 0f
        val pop = Spring.POP
        circle.animate().scaleX(1f).scaleY(1f).setStartDelay(120L + index * 35L).setDuration(pop.durationMs).setInterpolator(pop).start()
        box.animate().alpha(1f).setStartDelay(120L + index * 35L).setDuration(180).start()
        return box
    }

    fun showMenu() {
        val v = view ?: return
        main.removeCallbacks(collapseRunnable)
        readSettings()
        menu.layoutParams = menuParams()
        buildMenu()
        if (mode == Mode.NOTIF) disappear(content)
        mode = Mode.MENU
        applyWindow(big = true)
        animateIsland(ew.toFloat(), mh.toFloat(), 1f, opening = true)
        menu.visibility = View.VISIBLE
        menu.alpha = 1f
        menu.scaleX = 1f
        menu.scaleY = 1f
        main.postDelayed(collapseRunnable, 7000)
        Log.i(TAG, "Menu ouvert")
    }

    private fun collapse(reason: String) {
        Log.i(TAG, "Repli ($reason)")
        main.removeCallbacks(collapseRunnable)
        note = null
        if (mode == Mode.MENU) disappear(menu, gone = true) else disappear(content)
        mode = Mode.COLLAPSED
        applyWindow(big = false)
        animateIsland(cw.toFloat(), ch.toFloat(), 0f, opening = false)
    }

    // ---------- Gestes ----------

    override fun press(down: Boolean) {
        Log.i(TAG, "Geste appui=$down mode=$mode")
        if (down && mode != Mode.COLLAPSED) main.removeCallbacks(collapseRunnable)
        if (!down && mode == Mode.NOTIF) main.postDelayed(collapseRunnable, 4000)
        if (mode != Mode.COLLAPSED) return
        // L'ilot gonfle legerement sous le doigt, comme sur iPhone
        val k = if (down) 1.1f else 1f
        val s = if (down) Spring.SOFT else Spring.POP
        val v = view ?: return
        stopAnims()
        val a = spring(v.pw, cw * k, s) { v.pw = it }
        val b = spring(v.ph, ch * k, s) { v.ph = it }
        running.add(a); running.add(b)
        a.start(); b.start()
    }

    override fun tap() {
        Log.i(TAG, "Geste toucher mode=$mode")
        if (opening) return
        when (mode) {
            Mode.NOTIF -> openNote(floating = false)
            Mode.MENU -> collapse("toucher le menu")
            Mode.COLLAPSED -> {}
        }
    }

    override fun longPress() = showMenu()

    override fun drag(dy: Float) {
        if (mode != Mode.NOTIF || opening) return
        val v = view ?: return
        // Assez tire vers le bas : on ouvre tout de suite, sans attendre que le doigt se leve
        // (Android peut s'approprier un glissement parti du haut de l'ecran).
        if (dy > dp(56)) {
            Log.i(TAG, "Geste glisser vers le bas dy=$dy")
            openNote(floating = true)
            return
        }
        main.removeCallbacks(collapseRunnable)
        stopAnims()
        // Etirement elastique : resiste de plus en plus
        val stretch = if (dy > 0) sqrt(dy) * dp(3).toFloat() / 2f else dy * 0.5f
        v.ph = max(ch.toFloat(), min(eh + stretch, (eh + bottomPad - dp(2)).toFloat()))
        v.pw = ew + (if (dy > 0) min(dy * 0.05f, dp(10).toFloat()) else 0f)
        v.invalidate()
    }

    override fun release(dy: Float) {
        Log.i(TAG, "Geste relache dy=$dy mode=$mode")
        if (mode != Mode.NOTIF) {
            if (mode == Mode.COLLAPSED) press(false)
            return
        }
        when {
            dy > dp(30) -> openNote(floating = true)
            dy < -dp(24) -> collapse("glisser vers le haut")
            else -> {
                animateIsland(ew.toFloat(), eh.toFloat(), 1f, opening = true)
                main.postDelayed(collapseRunnable, 4000)
            }
        }
    }

    override fun outside() {
        Log.i(TAG, "Geste dehors mode=$mode")
        if (mode == Mode.MENU) collapse("toucher dehors")
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        main.removeCallbacksAndMessages(null)
        stopAnims()
        view?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        pad?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        removeBlur()
        view = null
        pad = null
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
                s.main.post { s.show(note.copy(pkg = pkg)) }
                return
            }
            if (!Prefs.enabled(ctx) || (!Settings.canDrawOverlays(ctx) && IslandAccessibility.instance == null)) return
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
