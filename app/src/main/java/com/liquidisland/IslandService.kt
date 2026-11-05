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
import android.graphics.Paint
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
    val autoCancel: Boolean = false,
    /** Action au toucher (sinon : ouvre l'appli de la notification) */
    val action: (() -> Unit)? = null
)

/** Gestes reconnus sur l'ilot. */
interface IslandGestures {
    fun press(down: Boolean)
    fun tap()
    fun longPress()
    fun drag(dy: Float)
    fun release(dy: Float)
    fun outside()
    /** Android a vole le geste (glissement depuis le haut de l'ecran) avant qu'on voie le mouvement. */
    fun stolen(heldMs: Long) {}
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
    private var downAt = 0L
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
                downAt = android.os.SystemClock.uptimeMillis()
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
                // Telephone charge : les mouvements peuvent arriver regroupes (ou pas du tout) avant le lever.
                // Le doigt a quand meme parcouru la distance : c'est un glissement, pas un toucher.
                if (!moved && e.actionMasked == MotionEvent.ACTION_UP && abs(e.y - downY) > slop * 2) {
                    moved = true
                    g?.press(false)
                }
                if (moved) {
                    // Android peut interrompre (CANCEL) un glissement parti du haut de l'ecran :
                    // on garde alors la distance deja parcourue pour reconnaitre le geste.
                    g?.release(if (e.actionMasked == MotionEvent.ACTION_UP) e.y - downY else lastDy)
                } else {
                    g?.press(false)
                    if (allowTap && e.actionMasked == MotionEvent.ACTION_UP && inside(e.x, e.y)) g?.tap()
                    if (e.actionMasked == MotionEvent.ACTION_CANCEL) g?.stolen(android.os.SystemClock.uptimeMillis() - downAt)
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

/**
 * Quand l'ecran tourne (video, jeu en paysage), l'ilot ne tourne pas : il reste colle a la camera
 * avec sa forme d'origine. Ce conteneur fait pivoter son enfant de [deg] (+-90) et echange largeur/hauteur.
 */
class Rotator(ctx: Context, private val child: View, private val deg: Float) : FrameLayout(ctx) {
    var portraitW = 0
    var portraitH = 0

    init {
        clipChildren = false
        addView(child)
        child.rotation = deg
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        child.measure(
            MeasureSpec.makeMeasureSpec(portraitW, MeasureSpec.EXACTLY),
            MeasureSpec.makeMeasureSpec(portraitH, MeasureSpec.EXACTLY)
        )
        setMeasuredDimension(portraitH, portraitW)
    }

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
        // Enfant centre, puis tourne autour de son centre : il occupe exactement la fenetre
        val cx = (r - l) / 2
        val cy = (b - t) / 2
        child.layout(cx - portraitW / 2, cy - portraitH / 2, cx + portraitW / 2, cy + portraitH / 2)
        child.pivotX = portraitW / 2f
        child.pivotY = portraitH / 2f
    }
}

/**
 * Bande invisible sur toute la barre d'etat : tirer vers le bas depuis la moitie gauche ouvre le centre
 * de notifications Wave Island, depuis la moitie droite le centre de controle (comme sur iPhone / HyperOS).
 * [stolen] = Android a pris le geste (son volet va s'ouvrir, il faudra le refermer).
 */
class EdgeStrip(ctx: Context, private val onDown: (left: Boolean) -> Unit, private val onTap: () -> Unit, private val onPull: (left: Boolean, stolen: Boolean) -> Unit) : View(ctx) {
    private val slop = ViewConfiguration.get(ctx).scaledTouchSlop
    private var downX = 0f
    private var downY = 0f
    private var fired = true

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> { downX = e.x; downY = e.y; fired = false; onDown(e.x < width / 2f) }
            MotionEvent.ACTION_MOVE -> {
                val dy = e.y - downY
                if (!fired && dy > slop * 1.5f && dy > abs(e.x - downX)) { fired = true; onPull(downX < width / 2f, false) }
            }
            MotionEvent.ACTION_UP -> if (!fired && e.y - downY > slop * 2) { fired = true; onPull(downX < width / 2f, false) } else onTap()
            MotionEvent.ACTION_CANCEL -> if (!fired) { fired = true; onPull(downX < width / 2f, true) }
        }
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

class IslandService : Service(), IslandGestures, android.hardware.SensorEventListener {

    private enum class Mode { COLLAPSED, NOTIF, MENU, LIVE, ASSIST }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var wm: WindowManager
    private var view: IslandView? = null
    private lateinit var lp: WindowManager.LayoutParams
    private var pad: TouchPad? = null
    /** Bande du haut (centres de notifications / de controle) */
    private var strip: EdgeStrip? = null
    /** Wave Theme : le dock facon iPad */
    private var dock: WaveDock? = null
    private var lpStrip: WindowManager.LayoutParams? = null
    /** Indicateur de volume facon iPhone */
    private var hud: VolumeHud? = null
    private var rootView: View? = null
    private var rootPad: View? = null
    private var rotation = 0
    private var probe: View? = null
    private var blurDialog: android.app.Dialog? = null
    private var blurKey = ""
    private var blurUnavailable = false

    // Reflets en temps reel (inclinaison) et fond capture pour la refraction
    private var sensors: android.hardware.SensorManager? = null
    private var lightX = 0f
    private var lightY = 0f
    private var screenOn = true
    /** Capture de l'ecran en direct (effet goutte sans delai), Android 13+ */
    private var live: LiveCapture? = null
    private val liveActive get() = live?.active == true
    /** Effet goutte en direct actif (pour l'ecran de reglages) */
    val isLive get() = liveActive
    private val statsRunnable = object : Runnable {
        override fun run() {
            live?.let { if (it.active) Log.i(TAG, "Fond en direct : ${it.received} images recues, ${it.used} utilisees (5 s)"); it.received = 0; it.used = 0 }
            if (liveActive) main.postDelayed(this, 5000)
        }
    }
    private var backdropBitmap: android.graphics.Bitmap? = null
    private var backdropBuffer: android.hardware.HardwareBuffer? = null
    private var captureFails = 0
    private val captureRunnable = object : Runnable {
        override fun run() {
            requestCapture()
            // Au rythme maximum permis par Android (une capture toutes les ~1/3 s)
            main.postDelayed(this, CAPTURE_MS)
        }
    }
    private val screenReceiver = object : android.content.BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            when (i.action) {
                Intent.ACTION_SCREEN_ON -> { screenOn = true; startLive() }
                Intent.ACTION_SCREEN_OFF -> { screenOn = false; stopLive() }
                // Telephone deverrouille : si Android a coupe la capture, on repropose l'autorisation
                Intent.ACTION_USER_PRESENT -> { askLiveAgain(); checkUpdateSoon() }
            }
        }
    }
    private var dark = true
    private lateinit var lpPad: WindowManager.LayoutParams

    private lateinit var content: LinearLayout
    private lateinit var iconView: ImageView
    private lateinit var titleView: TextView
    private lateinit var textView: TextView
    private lateinit var menu: LinearLayout

    // ---------- Activites en arriere-plan (musique, telechargements) ----------
    private var mediaWatcher: MediaWatcher? = null
    private var mediaNow: MediaNow? = null
    private val tasks = LinkedHashMap<String, ProgressNow>()
    private lateinit var compact: FrameLayout
    private lateinit var compactArt: ImageView
    private lateinit var eq: EqualizerView
    private lateinit var ring: RingView
    private lateinit var livePanel: LinearLayout

    // ---------- Assistant IA (version finale) ----------
    private lateinit var assistPanel: LinearLayout
    private lateinit var orb: OrbView
    private lateinit var aStatus: TextView
    private lateinit var aHeard: TextView
    private lateinit var aAnswer: TextView
    private lateinit var aScroll: MaxScroll
    private var aState = com.liquidisland.assist.Assistant.State.IDLE
    private var liveTimes: Triple<TextView, BarView, TextView>? = null
    private var livePlay: ImageView? = null
    private val liveRows = mutableListOf<Triple<ProgressNow, TextView, BarView>>()
    private var liveSig = ""
    private var lhMax = 0
    private fun hasLive() = mediaNow != null || tasks.isNotEmpty()
    /** Largeur de l'ilot au repos : elargi quand une activite tourne (pochette a gauche, barres a droite). */
    private fun restW() = if (hasLive()) cw + dp(COMPACT_EXTRA) else cw
    private fun winH() = max(max(eh, mh), lhMax) + bottomPad

    /**
     * Zone tactile de l'ilot replie : plus large que l'ilot et prolongee sous la barre d'etat,
     * pour que le doigt qui glisse vers le bas parte de chez nous et pas du volet d'Android.
     */
    private fun padW() = restW() + 2 * dp(16)
    private fun padH() = if (rotation == android.view.Surface.ROTATION_0 || rotation == android.view.Surface.ROTATION_180)
        max(ch + dp(8), statusBarHeight() - off + dp(34)) else ch + dp(8)
    private val liveTicker = object : Runnable {
        override fun run() {
            updateLiveProgress()
            if (mode == Mode.LIVE) main.postDelayed(this, 500)
        }
    }

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
        main.post {
            // L'ecran a tourne : on replace l'ilot contre la camera
            if (displayRotation() != rotation) { rehost(); main.post { live?.reconfigure() } } else applySettings()
        }
    }

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Îlot actif", NotificationManager.IMPORTANCE_MIN)
        )
        val notif = buildNotif()
        // Relance automatique en arriere-plan (apres que HyperOS a tue l'appli) : Android peut refuser
        // le premier plan. Sans cette protection, l'appli plantait et Android coupait l'accessibilite.
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(1, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                startForeground(1, notif)
            }
        } catch (e: Exception) {
            Log.w(TAG, "Premier plan refuse pour l'instant : $e")
        }
        if (!Settings.canDrawOverlays(this) && IslandAccessibility.instance == null) {
            stopSelf()
            return
        }
        createIsland()
        instance = this
        sensors = getSystemService(Context.SENSOR_SERVICE) as android.hardware.SensorManager
        registerReceiver(screenReceiver, android.content.IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_SCREEN_OFF)
            addAction(Intent.ACTION_USER_PRESENT)
        })
        screenOn = (getSystemService(Context.POWER_SERVICE) as android.os.PowerManager).isInteractive
        if (Build.VERSION.SDK_INT >= 33) live = LiveCapture(this, main, liveListener)
        if (screenOn) startLive()
        if (LiveCapture.pendingResult != null) startLiveCapture()
        updateNotif()
        mediaWatcher = MediaWatcher(this, main) { onMediaChanged() }.also { it.start() }
        // Wave Drop : envoi / reception de fichiers entre appareils
        com.liquidisland.drop.WaveDrop.init(this)
        com.liquidisland.drop.WaveDrop.uis.add(dropUi)
        try { com.liquidisland.drop.WaveDrop.start(this) } catch (e: Exception) { Log.w(TAG, "Wave Drop : demarrage impossible", e) }
        main.postDelayed(updateCheck, 15_000)
        NotifListener.replayTasks()
    }

    /** Accessibilite cochee dans les reglages mais pas connectee : Android l'a arretee (apres un plantage). */
    private fun accessibilityStopped(): Boolean =
        IslandAccessibility.instance == null &&
            Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
                ?.contains(packageName) == true

    // ---------- Effet goutte en direct ----------

    private fun buildNotif(): Notification {
        val b = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_info_details)
            .setOngoing(true)
        val needLive = Build.VERSION.SDK_INT >= 33 && Prefs.refraction(this) && !liveActive
        if (accessibilityStopped()) {
            b.setContentTitle("Wave Island : accessibilité arrêtée par Android")
            b.setContentText("Touche ici, désactive puis réactive Wave Island pour toucher l'îlot")
            b.setContentIntent(
                android.app.PendingIntent.getActivity(
                    this, 8, Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                    android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT
                )
            )
        } else if (needLive) {
            b.setContentTitle("Wave Island est actif")
            b.setContentText("Touche ici pour activer l'effet goutte d'eau en direct")
            b.setContentIntent(
                android.app.PendingIntent.getActivity(
                    this, 7, Intent(this, CaptureConsentActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                    android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT
                )
            )
        } else {
            b.setContentTitle(if (liveActive) "Wave Island : effet goutte en direct" else "Wave Island est actif")
        }
        return b.build()
    }

    private fun updateNotif() {
        try {
            (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).notify(1, buildNotif())
        } catch (e: Exception) { }
    }

    /** La capture en direct a ete coupee (verrouillage, HyperOS...) : on redemande l'autorisation. */
    private fun askLiveAgain() {
        if (Build.VERSION.SDK_INT < 33 || liveActive || !Prefs.refraction(this) || !Prefs.liveWanted(this)) return
        Log.i(TAG, "Capture en direct coupee : nouvelle demande d'autorisation")
        main.postDelayed({
            if (liveActive) return@postDelayed
            try {
                startActivity(Intent(this, CaptureConsentActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            } catch (e: Exception) {
                Log.w(TAG, "Demande d'autorisation impossible : $e")
            }
        }, 700)
    }

    /** Lance la capture en direct avec l'autorisation donnee par l'utilisateur. */
    private fun startLiveCapture() {
        val res = LiveCapture.pendingResult ?: return
        LiveCapture.pendingResult = null
        val lc = live ?: return
        if (lc.active) return
        // Android 14 : le service doit etre de type « capture d'ecran » AVANT de demarrer la capture
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(1, buildNotif(),
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            } else if (Build.VERSION.SDK_INT >= 29) {
                startForeground(1, buildNotif(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            }
        } catch (e: Exception) {
            Log.w(TAG, "Service de capture refuse : $e")
            return
        }
        if (lc.start(res.first, res.second)) {
            main.removeCallbacks(captureRunnable)
            waitingFresh = false
            fadeAnim?.cancel()
            view?.glass?.fade = 1f
            if (!screenOn) lc.pause()
            main.removeCallbacks(statsRunnable)
            main.postDelayed(statsRunnable, 5000)
        }
        updateNotif()
    }

    private val liveListener = object : LiveCapture.Listener {
        override fun onLiveFrame(bmp: android.graphics.Bitmap, sx: Float, sy: Float) {
            try {
                liveFrame(bmp, sx, sy)
            } catch (e: Exception) {
                Log.w(TAG, "Image du fond : $e")
            }
        }

        private fun liveFrame(bmp: android.graphics.Bitmap, sx: Float, sy: Float) {
            val v = view ?: return
            val toScreen = viewToScreen(v) ?: return
            toScreen.postScale(sx, sy)
            val m = android.graphics.Matrix()
            toScreen.invert(m)
            val shader = android.graphics.BitmapShader(bmp, Shader.TileMode.MIRROR, Shader.TileMode.MIRROR)
            shader.setLocalMatrix(m)
            v.glass.backdrop = shader
            if (v.glass.fade < 1f) { fadeAnim?.cancel(); v.glass.fade = 1f }
            v.invalidate()
            hud?.setBackdrop(bmp, sx, sy)
        }

        override fun ringPoints(): FloatArray? {
            // Indicateur de volume affiche : on prend toutes les images (il bouge et se dessine aussi)
            if (hud?.visible == true) return null
            val v = view ?: return null
            val r = v.glass.rect
            if (r.isEmpty) return null
            val toScreen = viewToScreen(v) ?: return null
            // Deux anneaux de points juste autour de l'ilot : c'est la que le verre va chercher le fond
            val out = ArrayList<Float>(96)
            for (d in floatArrayOf(dp(5).toFloat(), dp(16).toFloat())) {
                val l = r.left - d
                val t = r.top - d
                val rr = r.right + d
                val b = r.bottom + d
                val n = 12
                for (i in 0..n) {
                    val x = l + (rr - l) * i / n
                    out.add(x); out.add(t); out.add(x); out.add(b)
                }
                for (i in 1 until 4) {
                    val y = t + (b - t) * i / 4
                    out.add(l); out.add(y); out.add(rr); out.add(y)
                }
            }
            val pts = out.toFloatArray()
            toScreen.mapPoints(pts)
            return pts
        }

        override fun onLiveStopped() {
            // L'image en cours va etre liberee : l'ilot l'oublie tout de suite
            view?.glass?.backdrop = null
            view?.invalidate()
            updateNotif()
            main.removeCallbacks(statsRunnable)
            // Retour au mode capture toutes les 1/3 s
            if (screenOn && Prefs.refraction(this@IslandService)) startLive()
        }
    }

    /** Matrice vue de l'ilot -> pixels de l'ecran (rotation comprise). */
    private fun viewToScreen(v: IslandView): android.graphics.Matrix? {
        val root = rootView ?: v
        if (!root.isAttachedToWindow) return null
        val loc = IntArray(2)
        root.getLocationOnScreen(loc)
        val m = android.graphics.Matrix()
        if (root !== v) v.transformMatrixToGlobal(m)
        m.postTranslate(loc[0].toFloat(), loc[1].toFloat())
        return m
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_LIVE) {
            startLiveCapture()
            return START_STICKY
        }
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

    private var clearGlass = true
    private var fullscreen = false
    /** Ilot cache (plein ecran, mode « cacher ») */
    private val hidden get() = fullscreen && Prefs.fullscreen(this) == "hide" && mode == Mode.COLLAPSED

    private fun onStatusBar(visible: Boolean) {
        if (fullscreen == !visible) return
        fullscreen = !visible
        Log.i(TAG, "Plein ecran : ${if (fullscreen) "oui" else "non"}")
        main.post {
            if (mode == Mode.COLLAPSED) {
                applyWindow(big = false)
                val (w, h) = restSize()
                animateIsland(w, h, 0f, opening = !fullscreen)
            }
        }
    }

    /**
     * Detection du plein ecran : une fenetre invisible de 1 px, placee SOUS la barre d'etat
     * (la fenetre de l'ilot, au-dessus, ne voit pas la barre d'etat et ne peut pas savoir si elle est cachee).
     */
    private fun addProbe() {
        if (Build.VERSION.SDK_INT < 30 || probe != null || !Settings.canDrawOverlays(this)) return
        val appWm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val pv = View(this)
        // La fenetre temoin commence sous la barre d'etat ; si elle remonte tout en haut, la barre est cachee
        pv.viewTreeObserver.addOnGlobalLayoutListener {
            val loc = IntArray(2)
            pv.getLocationOnScreen(loc)
            val sbH = statusBarHeight()
            if (sbH > 0) onStatusBar(loc[1] >= sbH / 2)
        }
        val plp = WindowManager.LayoutParams(
            1, WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE,
            android.graphics.PixelFormat.TRANSLUCENT
        ).apply { gravity = Gravity.TOP or Gravity.START }
        try {
            appWm.addView(pv, plp)
            probe = pv
        } catch (e: Exception) {
            Log.w(TAG, "Detection du plein ecran impossible : $e")
        }
    }

    private fun removeProbe() {
        probe?.let { try { (getSystemService(Context.WINDOW_SERVICE) as WindowManager).removeView(it) } catch (e: Exception) { } }
        probe = null
    }

    @Suppress("DEPRECATION")
    private fun displayRotation(): Int =
        (getSystemService(Context.WINDOW_SERVICE) as WindowManager).defaultDisplay.rotation

    /** En paysage, la vue tourne pour garder l'orientation du telephone (ilot contre la camera). */
    private fun wrap(child: View, w: Int, h: Int): View {
        val deg = when (rotation) {
            android.view.Surface.ROTATION_90 -> -90f
            android.view.Surface.ROTATION_270 -> 90f
            else -> return child
        }
        return Rotator(this, child, deg).apply { portraitW = w; portraitH = h }
    }

    /** Place une fenetre : en haut au centre (portrait), ou contre le bord de la camera (paysage). */
    private fun place(p: WindowManager.LayoutParams, w: Int, h: Int) {
        when (rotation) {
            android.view.Surface.ROTATION_90 -> {
                p.gravity = Gravity.LEFT or Gravity.CENTER_VERTICAL
                p.x = off; p.y = 0; p.width = h; p.height = w
            }
            android.view.Surface.ROTATION_270 -> {
                p.gravity = Gravity.RIGHT or Gravity.CENTER_VERTICAL
                p.x = off; p.y = 0; p.width = h; p.height = w
            }
            else -> {
                p.gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
                p.x = 0; p.y = off; p.width = w; p.height = h
            }
        }
    }

    /** Taille de l'ilot au repos : normale, cachee, ou juste autour de la camera en plein ecran. */
    private fun restSize(): Pair<Float, Float> {
        if (!fullscreen) return restW().toFloat() to ch.toFloat()
        // En plein ecran l'ilot reste visible (contre la camera), sauf si on a choisi de le cacher
        return if (Prefs.fullscreen(this) == "hide") 0f to 0f else restW().toFloat() to ch.toFloat()
    }
    private fun ink() = if (dark) Color.WHITE else Glass.ISLAND_INK
    private fun inkSoft() = if (dark) Color.argb(205, 255, 255, 255) else Glass.ISLAND_INK_SOFT

    /** En verre transparent, le texte blanc garde une ombre douce pour rester lisible sur tous les fonds. */
    private fun styleText(t: TextView) {
        if (clearGlass) t.setShadowLayer(dp(5).toFloat(), 0f, dp(1).toFloat() / 2f, Color.argb(170, 0, 0, 0))
        else t.setShadowLayer(0f, 0f, 0f, 0)
    }

    private fun readSettings() {
        dark = Prefs.isDark(this)
        clearGlass = Prefs.isClear(this)
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
        // Activites en grand : lecteur (~150 dp) + 3 telechargements au plus
        lhMax = menuTop + dp(14) + dp(150) + 3 * dp(64) + dp(12)
    }

    private fun createIsland() {
        readSettings()
        rotation = displayRotation()
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
        createLiveViews()
        createAssistViews()
        val v = IslandView(this)
        v.addView(compact, compactParams())
        v.addView(content, contentParams())
        v.addView(menu, menuParams())
        v.addView(livePanel, liveParams())
        v.addView(assistPanel, liveParams())
        v.pw = restW().toFloat()
        v.ph = ch.toFloat()
        v.glass.notch = notch
        v.glass.dark = dark
        v.glass.clear = clearGlass
        styleText(titleView)
        styleText(textView)
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
        val r = wrap(v, ew + 2 * side, winH())
        rootView = r
        wm.addView(r, lp)
        if (acc != null) {
            val ls = WindowManager.LayoutParams().apply {
                copyFrom(lpPad)
                gravity = Gravity.TOP or Gravity.LEFT
                x = 0; y = 0
                width = WindowManager.LayoutParams.MATCH_PARENT
                height = statusBarHeight()
            }
            lpStrip = ls
            val st = EdgeStrip(this, { left -> arm { pullShade(left, false) } }, { disarm() }) { left, stolen -> disarm(); pullShade(left, stolen) }
            strip = st
            try { wm.addView(st, ls) } catch (e: Exception) { strip = null; Log.w(TAG, "Bande du haut : $e") }
            updateStrip()
            dock = WaveDock(this, wm, WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY).also { it.attach() }
        }
        val p = TouchPad(this, this)
        pad = p
        val rp = wrap(p, padW(), padH())
        rootPad = rp
        wm.addView(rp, lpPad)
        // L'indicateur de volume a besoin de l'accessibilite (boutons + affichage au-dessus de tout)
        if (acc != null) {
            val h = hud ?: VolumeHud(this, main).also { hud = it }
            h.dark = dark
            h.clear = clearGlass
            h.attach(acc, WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY, rotation)
        } else {
            hud?.detach()
        }
        addProbe()
        Log.i(TAG, "Ilot affiche (forme=${if (notch) "encoche" else "ile"}, au-dessus de la barre d'etat=$aboveStatusBar)")
    }

    private fun compactParams() =
        FrameLayout.LayoutParams(cw + dp(COMPACT_EXTRA), ch, Gravity.TOP or Gravity.CENTER_HORIZONTAL)

    private fun liveParams() =
        FrameLayout.LayoutParams(ew, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply {
            topMargin = menuTop
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

    // ---------- Reflets et refraction en direct ----------

    private fun startLive() {
        sensors?.let { sm ->
            val s = sm.getDefaultSensor(android.hardware.Sensor.TYPE_GRAVITY)
                ?: sm.getDefaultSensor(android.hardware.Sensor.TYPE_ACCELEROMETER)
            if (s != null) sm.registerListener(this, s, android.hardware.SensorManager.SENSOR_DELAY_GAME)
        }
        main.removeCallbacks(captureRunnable)
        if (liveActive) { live?.resume(); return }
        if (Prefs.refraction(this) && Build.VERSION.SDK_INT >= 33) main.post(captureRunnable)
    }

    private fun stopLive() {
        live?.pause()
        sensors?.unregisterListener(this)
        main.removeCallbacks(captureRunnable)
    }

    override fun onSensorChanged(e: android.hardware.SensorEvent) {
        // Gravite -> direction de la lumiere ; lissage pour un mouvement doux
        val tx = (-e.values[0] / 9.81f).coerceIn(-1f, 1f)
        val ty = (e.values[1] / 9.81f - 0.7f).coerceIn(-1f, 1f)
        val nx = lightX + (tx - lightX) * 0.18f
        val ny = lightY + (ty - lightY) * 0.18f
        if (kotlin.math.abs(nx - lightX) + kotlin.math.abs(ny - lightY) < 0.008f) return
        lightX = nx
        lightY = ny
        view?.let {
            it.glass.lightX = lightX
            it.glass.lightY = lightY
            it.invalidate()
        }
    }

    override fun onAccuracyChanged(s: android.hardware.Sensor?, a: Int) {}

    /** Capture l'ecran (via l'accessibilite) pour deformer le fond sur les bords de l'ilot. */
    private var lastCapture = 0L
    private var fadeAnim: ValueAnimator? = null
    private var waitingFresh = false

    /** Appele par l'accessibilite quand l'ecran change : on cache le bord deforme et on recapture au plus vite. */
    private var lastEvent = 0L

    fun onScreenChanged() {
        lastEvent = android.os.SystemClock.uptimeMillis()
        if (liveActive) return
        val v = view ?: return
        if (!Prefs.refraction(this) || v.glass.backdrop == null) return
        if (!waitingFresh) {
            waitingFresh = true
            animateFade(0f, 90)
        }
        // On attend que l'ecran soit immobile avant de recapturer (sinon le bord montrerait un contenu deja parti)
        main.removeCallbacks(captureRunnable)
        main.postDelayed(captureRunnable, QUIET_MS + 20)
    }

    private fun animateFade(to: Float, ms: Long) {
        val v = view ?: return
        fadeAnim?.cancel()
        fadeAnim = ValueAnimator.ofFloat(v.glass.fade, to).apply {
            duration = ms
            addUpdateListener { v.glass.fade = it.animatedValue as Float; v.invalidate() }
            start()
        }
    }

    private var captureGen = 0

    /** L'ilot retrecit : la derniere capture contient l'ancien grand ilot, on la cache. */
    private fun invalidateBackdrop() {
        captureGen++
        if (liveActive) { live?.force = true; return }
        if (view?.glass?.backdrop == null) return
        waitingFresh = true
        animateFade(0f, 60)
    }

    /** Animation terminee : on recapture tout de suite si le bord est cache. */
    private fun afterSettle() {
        if (!waitingFresh || !screenOn) return
        val wait = max(0L, lastCapture + CAPTURE_MS - android.os.SystemClock.uptimeMillis())
        main.removeCallbacks(captureRunnable)
        main.postDelayed(captureRunnable, wait + 30)
    }

    private fun requestCapture() {
        if (Build.VERSION.SDK_INT < 33 || !screenOn || view == null || liveActive) return
        // Pendant une animation, la capture contiendrait l'ilot en mouvement : on attend qu'il soit pose
        if (running.isNotEmpty()) return
        val gen0 = captureGen
        val now = android.os.SystemClock.uptimeMillis()
        if (now - lastCapture < CAPTURE_MS - 10) return
        // Ecran en mouvement (defilement, nouvelle appli) : pas de capture tant qu'il bouge
        if (now - lastEvent < QUIET_MS) return
        if (hidden) return
        lastCapture = now
        val acc = IslandAccessibility.instance ?: return
        try {
            acc.takeScreenshot(android.view.Display.DEFAULT_DISPLAY, mainExecutor,
                object : android.accessibilityservice.AccessibilityService.TakeScreenshotCallback {
                    override fun onSuccess(r: android.accessibilityservice.AccessibilityService.ScreenshotResult) {
                        val hb = r.hardwareBuffer
                        if (gen0 != captureGen || running.isNotEmpty() ||
                            android.os.SystemClock.uptimeMillis() - lastEvent < QUIET_MS) { hb.close(); afterSettle(); return }
                        val bmp = android.graphics.Bitmap.wrapHardwareBuffer(hb, r.colorSpace)
                        if (bmp == null) { hb.close(); return }
                        setBackdrop(bmp, hb)
                        if (captureFails > 0 || backdropCount++ == 0) Log.i(TAG, "Fond capture pour le verre")
                        captureFails = 0
                    }
                    override fun onFailure(code: Int) {
                        if (captureFails++ < 3) Log.w(TAG, "Capture du fond refusee (code $code)")
                    }
                })
        } catch (e: Exception) {
            if (captureFails++ < 3) Log.w(TAG, "Capture du fond impossible : $e")
        }
    }

    private var backdropCount = 0

    private fun setBackdrop(bmp: android.graphics.Bitmap, hb: android.hardware.HardwareBuffer) {
        val v = view
        if (v == null) { bmp.recycle(); hb.close(); return }
        // Vue -> ecran (rotation comprise), puis inverse : l'image de l'ecran tombe au bon endroit dans la vue
        val root = rootView ?: v
        val loc = IntArray(2)
        root.getLocationOnScreen(loc)
        val toScreen = android.graphics.Matrix()
        if (root !== v) v.transformMatrixToGlobal(toScreen)
        toScreen.postTranslate(loc[0].toFloat(), loc[1].toFloat())
        val m = android.graphics.Matrix()
        toScreen.invert(m)
        val shader = android.graphics.BitmapShader(bmp, Shader.TileMode.MIRROR, Shader.TileMode.MIRROR)
        shader.setLocalMatrix(m)
        val oldBmp = backdropBitmap
        val oldHb = backdropBuffer
        backdropBitmap = bmp
        backdropBuffer = hb
        v.glass.backdrop = shader
        v.invalidate()
        hud?.setBackdrop(bmp, 1f, 1f)
        // Nouvelle image du fond : le bord deforme reapparait en douceur
        if (waitingFresh || v.glass.fade < 1f) {
            waitingFresh = false
            animateFade(1f, 160)
        }
        // L'ancienne image peut encore etre en cours de dessin : on la libere un peu plus tard
        main.postDelayed({
            try { oldBmp?.recycle() } catch (e: Exception) { }
            try { oldHb?.close() } catch (e: Exception) { }
        }, 1000)
    }

    private fun releaseBackdrop() {
        view?.glass?.backdrop = null
        try { backdropBitmap?.recycle() } catch (e: Exception) { }
        try { backdropBuffer?.close() } catch (e: Exception) { }
        backdropBitmap = null
        backdropBuffer = null
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
        place(lp, ew + 2 * side, winH())
        lp.flags = if (big) lp.flags and noTouch.inv() else lp.flags or noTouch
        place(lpPad, padW(), padH())
        // Zone tactile active seulement quand l'ilot est replie ET visible
        lpPad.flags = if (big || hidden) lpPad.flags or noTouch else lpPad.flags and noTouch.inv()
        if (update) {
            (rootView as? Rotator)?.let { it.portraitW = ew + 2 * side; it.portraitH = winH() }
            (rootPad as? Rotator)?.let { it.portraitW = padW(); it.portraitH = padH() }
            rootView?.let { try { wm.updateViewLayout(it, lp) } catch (e: Exception) { } }
            rootPad?.let { try { wm.updateViewLayout(it, lpPad) } catch (e: Exception) { } }
        }
        updateStrip()
    }

    /** La bande du haut n'ecoute qu'ilot replie, en portrait, hors plein ecran, et si l'option est active. */
    fun updateStrip() {
        val portraitNow = rotation == android.view.Surface.ROTATION_0 || rotation == android.view.Surface.ROTATION_180
        dock?.update(!fullscreen && portraitNow)
        val st = strip ?: return
        val ls = lpStrip ?: return
        val portrait = rotation == android.view.Surface.ROTATION_0 || rotation == android.view.Surface.ROTATION_180
        val on = Prefs.shade(this) && !windowBig && !fullscreen && portrait
        val noTouch = WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        val f = if (on) ls.flags and noTouch.inv() else ls.flags or noTouch
        val h = statusBarHeight()
        if (f == ls.flags && h == ls.height) return
        ls.flags = f
        ls.height = h
        try { wm.updateViewLayout(st, ls) } catch (e: Exception) { }
    }

    /** Tirer la barre d'etat : notifications (gauche) ou controle (droite), facon Wave Island. */
    private fun pullShade(left: Boolean, stolen: Boolean) {
        Log.i(TAG, "Bord du haut tire a ${if (left) "gauche" else "droite"} (intercepte par Android=$stolen)")
        val open = {
            com.liquidisland.shade.ShadeActivity.open(this,
                if (left) com.liquidisland.shade.ShadeActivity.NOTIFS else com.liquidisland.shade.ShadeActivity.CONTROLS)
        }
        if (stolen) afterShade(open) else open()
    }

    /** Recree la fenetre (quand l'accessibilite s'active ou se coupe). */
    fun rehost() {
        main.post {
            updateNotif()
            stopAnims()
            main.removeCallbacks(collapseRunnable)
            rootView?.let { try { wm.removeView(it) } catch (e: Exception) { } }
            rootPad?.let { try { wm.removeView(it) } catch (e: Exception) { } }
            strip?.let { try { wm.removeView(it) } catch (e: Exception) { } }
            strip = null
            dock?.detach()
            dock = null
            rootView = null
            rootPad = null
            hud?.detach()
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

    /** Bouton de volume (depuis l'accessibilite). */
    fun onVolumeKey(e: android.view.KeyEvent): Boolean {
        val h = hud ?: return false
        if (!screenOn) return false
        val handled = h.onKey(e)
        // Indicateur visible : on prend toutes les images du fond pour son effet goutte
        if (handled) live?.force = true
        return handled
    }

    /** Apercu de l'indicateur de volume depuis les reglages. */
    fun previewVolume() {
        hud?.refresh()
        hud?.preview()
    }

    /** Applique les nouveaux reglages (taille, forme, position, raccourcis) sans redemarrer. */
    fun applySettings() {
        val v = view ?: return
        stopAnims()
        main.removeCallbacks(collapseRunnable)
        main.removeCallbacks(liveTicker)
        mediaNow = mediaWatcher?.current()
        if (!Prefs.liveDownloads(this)) tasks.clear()
        readSettings()
        mode = Mode.COLLAPSED
        note = null
        content.alpha = 0f
        content.visibility = View.INVISIBLE
        content.layoutParams = contentParams()
        menu.visibility = View.GONE
        menu.layoutParams = menuParams()
        livePanel.visibility = View.GONE
        livePanel.layoutParams = liveParams()
        compact.layoutParams = compactParams()
        liveSig = ""
        styleLive()
        refreshCompact()
        v.glass.notch = notch
        v.glass.dark = dark
        v.glass.clear = clearGlass
        v.glass.tint = 0f
        hud?.dark = dark
        hud?.clear = clearGlass
        hud?.refresh()
        titleView.setTextColor(ink())
        textView.setTextColor(inkSoft())
        styleText(titleView)
        styleText(textView)
        val wantBlur = Prefs.blur(this) && Build.VERSION.SDK_INT >= 31
        // La fenetre de flou doit etre SOUS l'ilot : on recree tout dans le bon ordre
        if (wantBlur && blurDialog == null && !blurUnavailable) { rehost(); return }
        if (!wantBlur) removeBlur()
        v.glass.blurred = blurDialog != null
        if (!Prefs.refraction(this)) { main.removeCallbacks(captureRunnable); live?.stop(); releaseBackdrop(); updateNotif() }
        else if (screenOn) { main.removeCallbacks(captureRunnable); if (Build.VERSION.SDK_INT >= 33) main.post(captureRunnable) }
        val (rw, rh) = restSize()
        v.pw = rw
        v.ph = rh
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
            // Une animation terminee ne compte plus (sinon les captures du fond resteraient bloquees)
            addListener(object : AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: Animator) {
                    running.remove(animation)
                    if (running.isEmpty()) afterSettle()
                }
            })
        }

    private fun animateIsland(w: Float, h: Float, tint: Float, opening: Boolean, onDone: (() -> Unit)? = null) {
        val v = view ?: return
        if (w < v.pw - 1f || h < v.ph - 1f) invalidateBackdrop()
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
                    if (left == 0 && myGen == gen) {
                        onDone?.invoke()
                        afterSettle()
                    }
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
        if (mode == Mode.ASSIST) {
            if (aState != com.liquidisland.assist.Assistant.State.DONE) { Log.i(TAG, "Notification gardee pour plus tard (assistant en cours) : ${n.title}"); return }
            leaveAssist()
        }
        main.removeCallbacks(collapseRunnable)
        iconView.setImageDrawable(n.icon)
        titleView.text = n.title
        titleView.visibility = if (n.title.isEmpty()) View.GONE else View.VISIBLE
        textView.text = n.text
        textView.visibility = if (n.text.isEmpty()) View.GONE else View.VISIBLE
        note = n

        if (!windowBig) applyWindow(big = true)
        if (mode == Mode.MENU) disappear(menu, gone = true)
        if (mode == Mode.LIVE) disappear(livePanel, gone = true)
        hideCompact()
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
        val h1 = min((eh + dp(40)).toFloat(), (winH() - dp(2)).toFloat())
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
                    running.remove(animation)
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
        n.action?.let { it(); return }
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
        val items = Shortcuts.load(this).mapNotNull { Shortcuts.resolve(this, it) }.toMutableList()
        // Mise a jour en attente : toujours en premier dans le menu
        if (Updater.latest > BuildConfig.VERSION_CODE)
            items.add(0, Shortcuts.Item(UPDATE_ITEM, "Mettre à jour", LineIcon(LineIcon.Kind.UPDATE, ink(), resources.displayMetrics.density), false))
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
                if (clearGlass) intArrayOf(Color.argb(30, 255, 255, 255), Color.argb(8, 255, 255, 255))
                else if (dark) intArrayOf(Color.argb(60, 255, 255, 255), Color.argb(22, 255, 255, 255))
                else intArrayOf(Color.argb(200, 255, 255, 255), Color.argb(140, 255, 255, 255))
            ).apply {
                shape = GradientDrawable.OVAL
                setStroke(max(1, dp(1)), if (clearGlass) Color.argb(130, 255, 255, 255) else if (dark) Color.argb(80, 255, 255, 255) else Color.argb(230, 255, 255, 255))
            }
            addView(ImageView(this@IslandService).apply {
                setImageDrawable(item.icon)
                if (item.id == UPDATE_ITEM) setPadding(dp(11), dp(11), dp(11), dp(11))
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
                styleText(this)
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
                    collapse("raccourci")
                    if (item.id == UPDATE_ITEM) updateFromIsland() else Shortcuts.run(this@IslandService, item.id)
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
        if (mode == Mode.LIVE) disappear(livePanel, gone = true)
        if (mode == Mode.ASSIST) leaveAssist()
        hideCompact()
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
        when (mode) {
            Mode.MENU -> disappear(menu, gone = true)
            Mode.LIVE -> { disappear(livePanel, gone = true); main.removeCallbacks(liveTicker) }
            Mode.ASSIST -> leaveAssist()
            else -> disappear(content)
        }
        mode = Mode.COLLAPSED
        applyWindow(big = false)
        val (rw, rh) = restSize()
        animateIsland(rw, rh, 0f, opening = false)
        refreshCompact()
    }

    // ---------- Gestes ----------

    override fun press(down: Boolean) {
        Log.i(TAG, "Geste appui=$down mode=$mode")
        // Doigt pose sur l'ilot replie : si le volet d'Android s'ouvre juste apres, c'est qu'il a vole le geste
        if (down && mode == Mode.COLLAPSED && !opening && com.liquidisland.drop.WaveDrop.enabled(this)) arm { openWaveDrop() }
        if (down && mode != Mode.COLLAPSED) main.removeCallbacks(collapseRunnable)
        if (!down && mode == Mode.NOTIF) main.postDelayed(collapseRunnable, 4000)
        if (mode != Mode.COLLAPSED) return
        // L'ilot gonfle legerement sous le doigt, comme sur iPhone
        val k = if (down) 1.1f else 1f
        val s = if (down) Spring.SOFT else Spring.POP
        val v = view ?: return
        stopAnims()
        val a = spring(v.pw, restW() * k, s) { v.pw = it }
        val b = spring(v.ph, ch * k, s) { v.ph = it }
        running.add(a); running.add(b)
        a.start(); b.start()
    }

    override fun tap() {
        disarm()
        Log.i(TAG, "Geste toucher mode=$mode")
        if (opening) return
        when (mode) {
            Mode.NOTIF -> openNote(floating = false)
            Mode.MENU -> collapse("toucher le menu")
            Mode.LIVE -> collapse("toucher les activites")
            Mode.ASSIST -> com.liquidisland.assist.Assistant.trigger(this)
            Mode.COLLAPSED -> if (hasLive()) showLive()
        }
    }

    override fun longPress() { disarm(); showMenu() }

    // Doigt pose (sur l'ilot ou la barre d'etat) : action a faire si le volet d'Android s'ouvre a la place
    private var armedAt = 0L
    private var armedThen: (() -> Unit)? = null
    private fun arm(then: () -> Unit) { armedAt = android.os.SystemClock.uptimeMillis(); armedThen = then }
    private fun disarm() { armedAt = 0L; armedThen = null }

    override fun drag(dy: Float) {
        if (mode == Mode.COLLAPSED && !opening && dy > 0 && com.liquidisland.drop.WaveDrop.enabled(this)) {
            // L'ilot s'etire comme une goutte avant de lacher le panneau Wave Drop
            val v = view ?: return
            stopAnims()
            v.ph = ch + min(sqrt(dy) * dp(3).toFloat() / 2f, dp(26).toFloat())
            v.pw = restW() - min(dy * 0.04f, dp(10).toFloat())
            v.invalidate()
            return
        }
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
        disarm()
        Log.i(TAG, "Geste relache dy=$dy mode=$mode")
        if (mode != Mode.NOTIF) {
            if (mode == Mode.COLLAPSED) {
                if (dy > dp(30) && com.liquidisland.drop.WaveDrop.enabled(this)) openWaveDrop() else press(false)
            }
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

    /**
     * Glissement vers le bas sur l'ilot replie que le volet d'Android a intercepte des le depart :
     * on referme le volet et on ouvre quand meme Wave Drop.
     */
    override fun stolen(heldMs: Long) {
        Log.i(TAG, "Geste intercepte par Android apres ${heldMs} ms (mode=$mode)")
        if (opening || heldMs > 1500) return
        if (mode == Mode.NOTIF) {
            // Meme chose pour une notification : glisser vers le bas l'ouvre en fenetre
            if (Build.VERSION.SDK_INT >= 31) IslandAccessibility.instance?.performGlobalAction(
                android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_DISMISS_NOTIFICATION_SHADE)
            openNote(floating = true)
            return
        }
        if (mode != Mode.COLLAPSED || !com.liquidisland.drop.WaveDrop.enabled(this)) return
        // Le volet (ou le centre de controle de HyperOS) va s'ouvrir : des qu'il apparait on le referme
        // (retour), puis le panneau coule depuis l'ilot. Sans volet detecte, on ouvre quand meme.
        afterShade { openWaveDrop() }
        // L'ilot s'etire deja : le geste est bien compris
        view?.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
    }

    private var shadePending = 0L
    private var shadeThen: (() -> Unit)? = null

    /** Le volet d'Android va s'ouvrir a notre place : on le refermera, puis [then]. */
    private fun afterShade(then: () -> Unit) {
        shadePending = android.os.SystemClock.uptimeMillis()
        shadeThen = then
        main.removeCallbacks(shadeFallback)
        main.postDelayed(shadeFallback, 1100)
    }

    private val shadeFallback = Runnable {
        if (shadePending == 0L) return@Runnable
        shadePending = 0L
        Log.i(TAG, "Aucun volet detecte, ouverture directe")
        if (Build.VERSION.SDK_INT >= 31) IslandAccessibility.instance?.performGlobalAction(
            android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_DISMISS_NOTIFICATION_SHADE)
        shadeThen?.invoke()
        shadeThen = null
    }

    /** Une appli passe au premier plan (pour les applis recentes du dock). */
    fun onForegroundApp(pkg: String) { try { dock?.onForeground(pkg) } catch (e: Exception) { } }

    /** Une fenetre du systeme (volet des notifications, centre de controle) vient de s'afficher. */
    fun onShadeShown() {
        // Certains telephones (HyperOS) ne previennent pas l'appli que le geste lui a ete pris :
        // le volet qui s'ouvre juste apres avoir pose le doigt sur l'ilot suffit a le savoir
        if (shadePending == 0L && armedAt != 0L && android.os.SystemClock.uptimeMillis() - armedAt < 1500) {
            shadePending = armedAt
            shadeThen = armedThen
            main.removeCallbacks(shadeFallback)
        }
        disarm()
        val t = shadePending
        if (t == 0L || android.os.SystemClock.uptimeMillis() - t > 2500) return
        shadePending = 0L
        main.removeCallbacks(shadeFallback)
        Log.i(TAG, "Volet ouvert par Android, on le referme")
        val acc = IslandAccessibility.instance
        acc?.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK)
        // Le centre de controle de HyperOS ne se ferme pas toujours avec « retour » : on insiste (sans effet si deja ferme)
        if (Build.VERSION.SDK_INT >= 31) main.postDelayed({
            try { acc?.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_DISMISS_NOTIFICATION_SHADE) } catch (e: Exception) { }
        }, 140)
        val then = shadeThen
        shadeThen = null
        main.postDelayed({ then?.invoke() }, 280)
    }

    override fun outside() {
        Log.i(TAG, "Geste dehors mode=$mode")
        if (mode == Mode.MENU || mode == Mode.LIVE) collapse("toucher dehors")
        if (mode == Mode.ASSIST && aState == com.liquidisland.assist.Assistant.State.DONE) collapse("toucher dehors")
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        mediaWatcher?.stop()
        mediaWatcher = null
        com.liquidisland.drop.WaveDrop.uis.remove(dropUi)
        com.liquidisland.drop.WaveDrop.stop()
        stopLive()
        live?.stop()
        live = null
        removeProbe()
        try { unregisterReceiver(screenReceiver) } catch (e: Exception) { }
        releaseBackdrop()
        main.removeCallbacksAndMessages(null)
        stopAnims()
        rootView?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        rootPad?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        rootView = null
        rootPad = null
        hud?.detach()
        hud = null
        removeBlur()
        view = null
        pad = null
        super.onDestroy()
    }

    // ---------- Wave Drop dans l'ilot ----------

    private val dropUi = object : com.liquidisland.drop.WaveDrop.Ui {
        private val icon get() = applicationInfo.loadIcon(packageManager)

        override fun request(r: com.liquidisland.drop.WaveDrop.Request) {
            // Un appareil veut envoyer (colle ou a cote) : le panneau s'ouvre tout seul pour accepter
            if (com.liquidisland.drop.WaveDrop.uis.none { it is com.liquidisland.drop.WaveDropActivity }) {
                try {
                    view?.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
                    com.liquidisland.drop.WaveDropActivity.open(this@IslandService, r.id)
                    Log.i(TAG, "Wave Drop : demande de ${r.from}, panneau ouvert")
                    return
                } catch (e: Exception) { Log.w(TAG, "Wave Drop : ouverture du panneau refusee : $e") }
            } else return
            show(IslandNote(
                "${r.from} veut t'envoyer un fichier",
                r.offer.name + " · " + com.liquidisland.drop.WaveDropActivity.size(r.offer.size) + " — touche pour répondre",
                icon, null, packageName,
                action = { com.liquidisland.drop.WaveDropActivity.open(this@IslandService, r.id) }
            ))
        }

        override fun progress(title: String, text: String, done: Long, total: Long) {
            task(ProgressNow("wave-drop", packageName, title, text,
                if (total > 0) (done * 1000 / total).toInt() else 0, 1000, total <= 0, icon, null), force = true)
        }

        override fun finished(title: String, text: String, ok: Boolean, received: android.net.Uri?, mime: String?) {
            taskGone("wave-drop")
            show(IslandNote(title, text + if (received != null) " — touche pour l'ouvrir" else "", icon, null, packageName,
                action = { if (received != null) com.liquidisland.drop.WaveDropActivity.openFile(this@IslandService, received, mime) }))
        }
    }

    /** Wave Drop active ou coupe (appli, pastille des reglages rapides) : l'ilot le montre 1,8 s. */
    fun dropToggled(on: Boolean) {
        if (mode == Mode.NOTIF || mode == Mode.MENU) return
        Log.i(TAG, "Wave Drop ${if (on) "active" else "coupe"} : affiche sur l'ilot")
        show(IslandNote(
            if (on) "Wave Drop activé" else "Wave Drop désactivé",
            if (on) "Visible des appareils proches" else "Plus visible",
            LineIcon(LineIcon.Kind.DROP, if (on) Glass.ACCENT else Color.WHITE, density), null, packageName, action = {}
        ))
        main.removeCallbacks(collapseRunnable)
        main.postDelayed(collapseRunnable, 1800)
    }

    /** Telephones colles (NFC) : l'ilot vibre et le menu Wave Drop en sort tout seul. */
    fun openWaveDropFromNfc() {
        main.post {
            Log.i(TAG, "Wave Drop : telephones colles, menu ouvert depuis l'ilot")
            if (mode != Mode.COLLAPSED) collapse("telephones colles")
            openWaveDrop()
        }
    }

    /** Glisser l'ilot replie vers le bas : le panneau Wave Drop coule depuis l'ilot. */
    private fun openWaveDrop() {
        Log.i(TAG, "Wave Drop : panneau ouvert depuis l'ilot")
        view?.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
        com.liquidisland.drop.WaveDropActivity.open(this)
        val (rw, rh) = restSize()
        animateIsland(rw, rh, 0f, opening = false)
    }

    // ---------- Mises a jour depuis l'ilot ----------

    // Verification legere (un fichier texte de quelques octets) : toutes les 30 min, et a chaque
    // deverrouillage si la derniere date de plus de 10 min. Une version deja proposee est reproposee
    // au plus une fois par heure, et reste toujours accessible dans le menu (appui long).
    private var updateOffered = 0
    private var updateOfferedAt = 0L
    private var updateCheckedAt = 0L
    private val updateCheck = object : Runnable {
        override fun run() {
            main.removeCallbacks(this)
            updateCheckedAt = System.currentTimeMillis()
            Updater.peek(this@IslandService) { v -> if (v != null) offerUpdate(v) }
            main.postDelayed(this, 30 * 60_000L)
        }
    }

    private fun checkUpdateSoon() {
        if (System.currentTimeMillis() - updateCheckedAt < 10 * 60_000L) {
            // Deja verifie recemment : on repropose seulement si une version attend
            if (Updater.latest > BuildConfig.VERSION_CODE) main.postDelayed({ offerUpdate(Updater.latest) }, 2500)
            return
        }
        main.postDelayed(updateCheck, 4000) // laisse le temps au reseau de revenir apres le deverrouillage
    }

    /** Nouvelle version : l'ilot la propose, un toucher la telecharge et l'installe. */
    fun offerUpdate(version: Int) {
        val now = System.currentTimeMillis()
        if (version == updateOffered && (mode == Mode.NOTIF || now - updateOfferedAt < 3600_000L)) return
        if (mode == Mode.MENU || mode == Mode.NOTIF) { main.postDelayed({ offerUpdate(version) }, 20_000); return }
        updateOffered = version
        updateOfferedAt = now
        Log.i(TAG, "Mise a jour proposee sur l'ilot : $version")
        show(IslandNote(
            "Mise à jour disponible",
            "Wave Island 3.0.${version - 100} — touche pour l'installer",
            applicationInfo.loadIcon(packageManager), null, packageName,
            action = { updateFromIsland() }
        ))
    }

    private fun updateFromIsland() {
        val icon = applicationInfo.loadIcon(packageManager)
        Log.i(TAG, "Mise a jour lancee depuis l'ilot")
        Updater.check(this) { s ->
            when (s) {
                is Updater.State.Available -> task(ProgressNow("wave-update", packageName, "Mise à jour Wave Island", "Préparation…", 0, 100, true, icon, null), force = true)
                is Updater.State.Downloading -> task(ProgressNow("wave-update", packageName, "Mise à jour Wave Island", "Téléchargement…", s.percent, 100, false, icon, null), force = true)
                is Updater.State.Installing -> {
                    taskGone("wave-update")
                    show(IslandNote("Installation…", "Wave Island redémarre toute seule", icon, null, packageName, action = {}))
                }
                is Updater.State.NeedsPermission -> {
                    taskGone("wave-update")
                    show(IslandNote("Une autorisation est nécessaire", "Touche : autorise Wave Island à installer ses mises à jour", icon, null, packageName, action = {
                        try {
                            startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, android.net.Uri.parse("package:$packageName"))
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                        } catch (e: Exception) { }
                    }))
                }
                is Updater.State.Error -> {
                    taskGone("wave-update")
                    show(IslandNote("Mise à jour impossible", s.message, icon, null, packageName, action = {}))
                }
                is Updater.State.UpToDate -> taskGone("wave-update")
            }
        }
    }

    // ---------- Activites en arriere-plan ----------

    /** L'acces aux notifications vient d'etre donne : on suit aussi la musique. */
    fun onListenerReady() = main.post { mediaWatcher?.start() }

    private fun onMediaChanged() {
        mediaNow = mediaWatcher?.current()
        liveChanged()
    }

    /** Une tache avec une barre de progression a change (depuis l'acces aux notifications). */
    fun task(p: ProgressNow, force: Boolean = false) = main.post {
        if (!force && !Prefs.liveDownloads(this)) { if (tasks.isNotEmpty()) { tasks.clear(); liveChanged() }; return@post }
        val isNew = !tasks.containsKey(p.key)
        tasks[p.key] = p
        if (isNew) Log.i(TAG, "Tache en cours : ${p.title} (${p.pkg})")
        liveChanged()
    }

    fun taskGone(key: String) = main.post {
        if (tasks.remove(key) != null) liveChanged()
    }

    private var hadLive = false

    private fun liveChanged() {
        if (view == null) return
        val has = hasLive()
        if (has != hadLive) {
            hadLive = has
            Log.i(TAG, if (has) "Activite en cours affichee" else "Plus d'activite en cours")
        }
        when (mode) {
            Mode.COLLAPSED -> {
                applyWindow(big = false)
                val (rw, rh) = restSize()
                val v = view
                if (v != null && (kotlin.math.abs(v.pw - rw) > 1f || kotlin.math.abs(v.ph - rh) > 1f) && !opening)
                    animateIsland(rw, rh, 0f, opening = rw > v.pw)
                refreshCompact()
            }
            Mode.ASSIST -> {}
            Mode.LIVE -> {
                if (!has) collapse("plus d'activite")
                else {
                    buildLive()
                    animateIsland(ew.toFloat(), liveHeight().toFloat(), 1f, opening = true)
                }
            }
            else -> refreshCompact()
        }
    }

    private fun createLiveViews() {
        compactArt = ImageView(this).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            background = GradientDrawable().apply { cornerRadius = dp(7).toFloat(); setColor(Color.argb(70, 255, 255, 255)) }
            clipToOutline = true
        }
        eq = EqualizerView(this)
        ring = RingView(this)
        compact = FrameLayout(this).apply {
            addView(compactArt, FrameLayout.LayoutParams(dp(24), dp(24), Gravity.START or Gravity.CENTER_VERTICAL).apply { marginStart = dp(7) })
            addView(eq, FrameLayout.LayoutParams(dp(19), dp(15), Gravity.END or Gravity.CENTER_VERTICAL).apply { marginEnd = dp(13) })
            addView(ring, FrameLayout.LayoutParams(dp(21), dp(21), Gravity.END or Gravity.CENTER_VERTICAL).apply { marginEnd = dp(9) })
            alpha = 0f
            visibility = View.INVISIBLE
        }
        livePanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(12))
            visibility = View.GONE
        }
        styleLive()
    }

    private fun styleLive() {
        if (!::eq.isInitialized) return
        eq.color = ink()
        ring.color = ink()
    }

    /** Pochette + barres (musique) ou anneau (telechargement) sur l'ilot replie. */
    private fun refreshCompact() {
        if (!::compact.isInitialized) return
        val m = mediaNow
        val t = tasks.values.firstOrNull()
        val show = mode == Mode.COLLAPSED && hasLive() && !hidden
        if (m != null) {
            if (m.art != null) { compactArt.setImageBitmap(m.art); compactArt.imageTintList = null; compactArt.setPadding(0, 0, 0, 0) }
            else { compactArt.setImageResource(android.R.drawable.ic_media_play); compactArt.imageTintList = android.content.res.ColorStateList.valueOf(ink()); compactArt.setPadding(dp(4), dp(4), dp(4), dp(4)) }
        } else if (t != null) {
            compactArt.setImageDrawable(t.icon)
            compactArt.imageTintList = null
            compactArt.setPadding(dp(2), dp(2), dp(2), dp(2))
        }
        eq.playing = m?.playing == true
        eq.visibility = if (t == null && m != null) View.VISIBLE else View.GONE
        ring.visibility = if (t != null) View.VISIBLE else View.GONE
        if (t != null) ring.fraction = t.fraction
        if (show) {
            if (compact.visibility != View.VISIBLE || compact.alpha < 1f) {
                compact.visibility = View.VISIBLE
                compact.animate().cancel()
                compact.animate().alpha(1f).setStartDelay(120).setDuration(220).start()
            }
        } else hideCompact()
    }

    private fun hideCompact() {
        if (!::compact.isInitialized || compact.visibility != View.VISIBLE) return
        compact.animate().cancel()
        compact.animate().alpha(0f).setStartDelay(0).setDuration(110).withEndAction { compact.visibility = View.INVISIBLE }.start()
    }

    private fun liveHeight(): Int {
        val spec = View.MeasureSpec.makeMeasureSpec(ew, View.MeasureSpec.EXACTLY)
        livePanel.measure(spec, View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED))
        return min(menuTop + livePanel.measuredHeight, winH() - bottomPad)
    }

    // ---------- Assistant IA ----------

    private fun createAssistViews() {
        orb = OrbView(this)
        aStatus = TextView(this).apply {
            textSize = 15f
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setTextColor(ink())
            styleText(this)
        }
        aHeard = TextView(this).apply {
            textSize = 13.5f
            setTypeface(typeface, Typeface.ITALIC)
            setTextColor(inkSoft())
            styleText(this)
        }
        aAnswer = TextView(this).apply {
            textSize = 15f
            setLineSpacing(dp(2).toFloat(), 1f)
            setTextColor(ink())
            styleText(this)
        }
        aScroll = MaxScroll(this, dp(230)).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            addView(aAnswer)
        }
        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(aStatus)
            addView(aHeard)
            addView(aScroll, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(4) })
        }
        assistPanel = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(dp(18), dp(12), dp(20), dp(16))
            addView(orb, LinearLayout.LayoutParams(dp(30), dp(30)).apply { marginEnd = dp(12); topMargin = dp(1) })
            addView(col, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            visibility = View.GONE
        }
    }

    private fun assistHeight(): Int {
        val spec = View.MeasureSpec.makeMeasureSpec(ew, View.MeasureSpec.EXACTLY)
        assistPanel.measure(spec, View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED))
        return min(menuTop + assistPanel.measuredHeight, winH() - bottomPad)
    }

    /** Appui long sur le bouton (assistant du telephone) : l'ilot se reveille et ecoute. */
    fun startAssist() = main.post { com.liquidisland.assist.Assistant.trigger(this) }

    /** Toucher l'ecran pendant l'ecoute : l'ilot se referme. */
    fun closeAssist() = main.post { if (mode == Mode.ASSIST) collapse("assistant annule") }

    /**
     * L'assistant dans l'ilot : une bulle de couleur qui respire (ecoute), tourne (reflexion) ou pulse (voix),
     * ce qui a ete entendu, puis la reponse.
     */
    fun showAssist(status: String, heard: String, answer: String?, state: com.liquidisland.assist.Assistant.State, done: Boolean) = main.post {
        val v = view ?: return@post
        aState = if (done) com.liquidisland.assist.Assistant.State.DONE else state
        aStatus.text = status
        aStatus.visibility = if (status.isEmpty()) View.GONE else View.VISIBLE
        aHeard.text = if (heard.isBlank()) "" else "« $heard »"
        aHeard.visibility = if (heard.isBlank()) View.GONE else View.VISIBLE
        val newAnswer = aAnswer.text.toString() != (answer ?: "")
        aAnswer.text = answer ?: ""
        aScroll.visibility = if (answer.isNullOrBlank()) View.GONE else View.VISIBLE
        if (newAnswer) aScroll.scrollTo(0, 0)
        for (t in listOf(aStatus, aAnswer)) { t.setTextColor(ink()); styleText(t) }
        aHeard.setTextColor(inkSoft()); styleText(aHeard)
        orb.state = aState
        main.removeCallbacks(collapseRunnable)
        if (mode != Mode.ASSIST) {
            if (mode == Mode.MENU) disappear(menu, gone = true)
            if (mode == Mode.LIVE) { disappear(livePanel, gone = true); main.removeCallbacks(liveTicker) }
            if (mode == Mode.NOTIF) disappear(content)
            hideCompact()
            mode = Mode.ASSIST
            applyWindow(big = true)
            assistPanel.layoutParams = liveParams()
            appear(assistPanel, if (notch) -dp(6).toFloat() else 0f)
            v.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
            animateIsland(ew.toFloat(), assistHeight().toFloat(), 1f, opening = true)
        } else {
            val h = assistHeight().toFloat()
            if (kotlin.math.abs(v.ph - h) > dp(3)) animateIsland(ew.toFloat(), h, 1f, opening = true)
        }
        if (done) main.postDelayed(collapseRunnable, min(20_000L, 6_000L + (answer?.length ?: 0) * 45L))
    }

    private fun leaveAssist() {
        disappear(assistPanel, gone = true)
        orb.state = com.liquidisland.assist.Assistant.State.IDLE
        if (aState != com.liquidisland.assist.Assistant.State.IDLE) com.liquidisland.assist.Assistant.close()
        aState = com.liquidisland.assist.Assistant.State.IDLE
    }

    fun showLive() {
        val v = view ?: return
        if (!hasLive()) return
        if (mode == Mode.ASSIST) leaveAssist()
        main.removeCallbacks(collapseRunnable)
        hideCompact()
        buildLive()
        mode = Mode.LIVE
        applyWindow(big = true)
        animateIsland(ew.toFloat(), liveHeight().toFloat(), 1f, opening = true)
        livePanel.layoutParams = liveParams()
        appear(livePanel, if (notch) -dp(6).toFloat() else 0f)
        main.removeCallbacks(liveTicker)
        main.post(liveTicker)
        main.postDelayed(collapseRunnable, 8000)
        v.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
        Log.i(TAG, "Activites ouvertes")
    }

    private fun liveText(s: String, size: Float, soft: Boolean = false, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(if (soft) inkSoft() else ink())
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        maxLines = 1
        ellipsize = TextUtils.TruncateAt.END
        styleText(this)
    }

    private fun clock(ms: Long): String {
        val s = max(0L, ms / 1000)
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, (s / 60) % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
    }

    private fun sendSafely(pi: PendingIntent?, pkg: String?) {
        var ok = false
        pi?.let {
            try {
                val o = ActivityOptions.makeBasic()
                if (Build.VERSION.SDK_INT >= 34) {
                    @Suppress("DEPRECATION")
                    o.setPendingIntentBackgroundActivityStartMode(ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
                }
                it.send(this, 0, null, null, null, null, o.toBundle()); ok = true
            } catch (e: Exception) { }
        }
        if (!ok && pkg != null) packageManager.getLaunchIntentForPackage(pkg)?.let {
            it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            try { startActivity(it) } catch (e: Exception) { }
        }
        collapse("ouvrir l'activite")
    }

    /** Lecteur (pochette, titre, temps, commandes) et une ligne par telechargement. */
    private fun buildLive() {
        val m = mediaNow
        val sig = (m?.let { "${it.title}|${it.artist}|${it.app}|${it.art != null}|${it.duration > 0}" } ?: "-") + "#" + tasks.keys.joinToString(",")
        if (sig == liveSig && livePanel.childCount > 0) { updateLiveProgress(); return }
        liveSig = sig
        livePanel.removeAllViews()
        liveTimes = null
        livePlay = null
        liveRows.clear()
        if (m != null) {
            val head = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                isClickable = true
                setOnClickListener { sendSafely(m.controller.sessionActivity, m.controller.packageName) }
            }
            head.addView(ImageView(this).apply {
                scaleType = ImageView.ScaleType.CENTER_CROP
                background = GradientDrawable().apply { cornerRadius = dp(14).toFloat(); setColor(Color.argb(60, 255, 255, 255)) }
                clipToOutline = true
                if (m.art != null) setImageBitmap(m.art) else {
                    setImageResource(android.R.drawable.ic_media_play)
                    imageTintList = android.content.res.ColorStateList.valueOf(ink())
                    setPadding(dp(14), dp(14), dp(14), dp(14))
                }
            }, LinearLayout.LayoutParams(dp(56), dp(56)).apply { marginEnd = dp(12) })
            val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            col.addView(liveText(m.title, 15f, bold = true))
            if (m.artist.isNotBlank()) col.addView(liveText(m.artist, 13f, soft = true))
            col.addView(liveText(m.app, 11f, soft = true).apply { alpha = 0.8f })
            head.addView(col, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            livePanel.addView(head)

            if (m.duration > 0) {
                val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
                val t0 = liveText("0:00", 11f, soft = true)
                val bar = BarView(this).apply { color = ink() }
                val t1 = liveText("", 11f, soft = true)
                row.addView(t0, LinearLayout.LayoutParams(dp(40), ViewGroup.LayoutParams.WRAP_CONTENT))
                row.addView(bar, LinearLayout.LayoutParams(0, dp(4), 1f))
                row.addView(t1.apply { gravity = Gravity.END }, LinearLayout.LayoutParams(dp(44), ViewGroup.LayoutParams.WRAP_CONTENT))
                livePanel.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) })
                liveTimes = Triple(t0, bar, t1)
            }

            val ctl = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
            fun btn(res: Int, size: Int, act: () -> Unit) = ImageView(this).apply {
                setImageResource(res)
                imageTintList = android.content.res.ColorStateList.valueOf(ink())
                scaleType = ImageView.ScaleType.FIT_CENTER
                val p = (dp(48) - dp(size)) / 2
                setPadding(p, p, p, p)
                isClickable = true
                background = android.graphics.drawable.RippleDrawable(
                    android.content.res.ColorStateList.valueOf(Color.argb(60, 255, 255, 255)), null,
                    GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(Color.WHITE) })
                setOnClickListener {
                    it.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                    act()
                    main.removeCallbacks(collapseRunnable)
                    main.postDelayed(collapseRunnable, 8000)
                }
            }
            val tc = m.controller.transportControls
            ctl.addView(btn(android.R.drawable.ic_media_previous, 24) { tc.skipToPrevious() }, LinearLayout.LayoutParams(dp(56), dp(48)))
            val play = btn(if (m.playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play, 30) {
                val playing = mediaNow?.playing == true
                if (playing) tc.pause() else tc.play()
                livePlay?.setImageResource(if (playing) android.R.drawable.ic_media_play else android.R.drawable.ic_media_pause)
            }
            livePlay = play
            ctl.addView(play, LinearLayout.LayoutParams(dp(64), dp(48)))
            ctl.addView(btn(android.R.drawable.ic_media_next, 24) { tc.skipToNext() }, LinearLayout.LayoutParams(dp(56), dp(48)))
            livePanel.addView(ctl, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48)).apply { topMargin = dp(6) })
        }

        tasks.values.take(3).forEachIndexed { i, t ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                isClickable = true
                setOnClickListener { sendSafely(t.intent, t.pkg) }
            }
            row.addView(ImageView(this).apply { setImageDrawable(t.icon) }, LinearLayout.LayoutParams(dp(32), dp(32)).apply { marginEnd = dp(10) })
            val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            col.addView(liveText(t.title, 14f, bold = true))
            val info = liveText(t.text, 12f, soft = true)
            col.addView(info)
            row.addView(col, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            val pct = liveText("", 13f, bold = true)
            row.addView(pct, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(8) })
            val top = if (i == 0 && m == null) 0 else dp(12)
            livePanel.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = top })
            val bar = BarView(this).apply { color = ink() }
            livePanel.addView(bar, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(4)).apply { topMargin = dp(6); marginStart = dp(42) })
            liveRows.add(Triple(t, pct, bar))
            liveRows[liveRows.size - 1].second.tag = info
        }
        updateLiveProgress()
    }

    private fun updateLiveProgress() {
        val m = mediaNow
        liveTimes?.let { (t0, bar, t1) ->
            if (m != null && m.duration > 0) {
                val now = m.now()
                t0.text = clock(now)
                t1.text = "-" + clock(m.duration - now)
                bar.fraction = now.toFloat() / m.duration
            }
        }
        if (m != null) livePlay?.setImageResource(if (m.playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play)
        for (i in liveRows.indices) {
            val (old, pct, bar) = liveRows[i]
            val t = tasks[old.key] ?: continue
            val f = t.fraction
            pct.text = if (f >= 0f) "${(f * 100).toInt()} %" else ""
            bar.fraction = f
            (pct.tag as? TextView)?.text = t.text
        }
    }

    companion object {
        private const val COMPACT_EXTRA = 92
        private const val TAG = "LiquidIsland"
        private const val CHANNEL = "island"
        private const val CAPTURE_MS = 340L
        private const val QUIET_MS = 350L
        const val ACTION_SHOW = "com.liquidisland.SHOW"
        const val ACTION_LIVE = "com.liquidisland.LIVE"
        const val EXTRA_TITLE = "title"
        const val EXTRA_TEXT = "text"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_PI = "pi"

        @Volatile
        var instance: IslandService? = null
            private set
        private const val UPDATE_ITEM = "wave:update"

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


/** Defilement limite en hauteur (longue reponse de l'assistant). */
class MaxScroll(ctx: Context, private val maxH: Int) : android.widget.ScrollView(ctx) {
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        super.onMeasure(widthMeasureSpec, View.MeasureSpec.makeMeasureSpec(maxH, View.MeasureSpec.AT_MOST))
    }
}

/** La bulle de l'assistant : degrade cyan / violet / rose qui respire, tourne ou pulse selon l'etat. */
class OrbView(ctx: Context) : View(ctx) {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glow = Paint(Paint.ANTI_ALIAS_FLAG)
    private var t0 = android.os.SystemClock.uptimeMillis()
    var state = com.liquidisland.assist.Assistant.State.IDLE
        set(v) { field = v; t0 = android.os.SystemClock.uptimeMillis(); invalidate() }

    override fun onDraw(c: Canvas) {
        val t = (android.os.SystemClock.uptimeMillis() - t0) / 1000f
        val S = com.liquidisland.assist.Assistant.State
        val (period, amp) = when (state) {
            S.LISTENING -> 1.25f to 0.13f
            S.SPEAKING -> 0.5f to 0.08f
            S.THINKING -> 0.9f to 0.06f
            else -> 1f to 0f
        }
        val k = 1f + amp * kotlin.math.sin(t * 2f * Math.PI.toFloat() / period)
        val cx = width / 2f
        val cy = height / 2f
        val r = min(width, height) / 2f * 0.78f * k
        val angle = if (state == S.THINKING) t * 400f else t * 40f
        c.save()
        c.rotate(angle, cx, cy)
        val colors = intArrayOf(Color.rgb(0x5E, 0xE7, 0xFF), Color.rgb(0x7B, 0x6C, 0xFF), Color.rgb(0xFF, 0x7A, 0xC8), Color.rgb(0x5E, 0xE7, 0xFF))
        paint.shader = android.graphics.SweepGradient(cx, cy, colors, null)
        glow.color = Color.argb(90, 0x8A, 0x7C, 0xFF)
        c.drawCircle(cx, cy, r * 1.22f, glow)
        c.drawCircle(cx, cy, r, paint)
        c.restore()
        if (state != S.IDLE && state != S.DONE) postInvalidateOnAnimation()
    }
}
