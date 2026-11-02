package com.liquidisland

import android.animation.ValueAnimator
import android.app.Dialog
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
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
import android.view.View
import android.view.ViewGroup
import android.view.Window
import android.view.WindowManager
import android.view.animation.DecelerateInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

data class IslandNote(
    val title: String,
    val text: String,
    val icon: Drawable?,
    val intent: PendingIntent?
)

class IslandService : Service() {

    private val main = Handler(Looper.getMainLooper())
    private var dialog: Dialog? = null
    private var blurOn = false

    private lateinit var content: LinearLayout
    private lateinit var iconView: ImageView
    private lateinit var titleView: TextView
    private lateinit var textView: TextView

    private var curW = 0
    private var curH = 0
    private var currentIntent: PendingIntent? = null
    private var anim: ValueAnimator? = null
    private val collapseRunnable = Runnable { collapse() }

    private val density get() = resources.displayMetrics.density
    private fun dp(v: Float) = (v * density).toInt()

    private val collapsedW get() = dp(118f)
    private val collapsedH get() = dp(34f)
    private val expandedH get() = dp(76f)
    private val expandedW get() = minOf(dp(360f), resources.displayMetrics.widthPixels - dp(24f))

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
        createIsland()
        instance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_SHOW && dialog != null) {
            val pkg = intent.getStringExtra(EXTRA_PKG) ?: packageName
            val icon = try { packageManager.getApplicationIcon(pkg) } catch (e: Exception) { null }
            @Suppress("DEPRECATION")
            val pi = intent.getParcelableExtra<PendingIntent>(EXTRA_PI)
            show(
                IslandNote(
                    intent.getStringExtra(EXTRA_TITLE).orEmpty(),
                    intent.getStringExtra(EXTRA_TEXT).orEmpty(),
                    icon,
                    pi
                )
            )
        }
        return START_STICKY
    }

    // ---------- Fenetre de l'ilot ----------

    private fun createIsland() {
        iconView = ImageView(this).apply {
            layoutParams = LinearLayout.LayoutParams(dp(32f), dp(32f))
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
                marginStart = dp(12f)
            }
        }
        content = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(18f), 0, dp(18f), 0)
            addView(iconView)
            addView(column)
            alpha = 0f
            visibility = View.INVISIBLE
        }
        val root = FrameLayout(this).apply {
            addView(
                content,
                FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            )
            setOnClickListener { onTap() }
        }

        // Une fenetre a part, de la taille exacte de la capsule : le flou ne touche que la capsule.
        val d = Dialog(this, android.R.style.Theme_Panel)
        d.requestWindowFeature(Window.FEATURE_NO_TITLE)
        d.setContentView(root)
        d.setCancelable(false)
        val win = d.window!!
        win.setType(WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY)
        win.clearFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
        win.addFlags(
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
        )
        win.setWindowAnimations(0)
        win.setGravity(Gravity.TOP or Gravity.CENTER_HORIZONTAL)
        if (Build.VERSION.SDK_INT >= 30) win.setDecorFitsSystemWindows(false)

        curW = collapsedW
        curH = collapsedH
        val lp = win.attributes
        lp.width = curW
        lp.height = curH
        lp.y = dp(8f)
        if (Build.VERSION.SDK_INT >= 28) {
            lp.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        if (Build.VERSION.SDK_INT >= 30) lp.fitInsetsTypes = 0
        win.attributes = lp

        blurOn = false
        if (Build.VERSION.SDK_INT >= 31 && Prefs.blur(this)) {
            val wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
            if (wm.isCrossWindowBlurEnabled) {
                win.setBackgroundBlurRadius(dp(22f))
                blurOn = true
            }
        }
        win.setBackgroundDrawable(Glass.island(density, curW, curH, blurOn))

        d.show()
        win.decorView.setPadding(0, 0, 0, 0)
        dialog = d
        Log.i(TAG, "Ilot affiche (flou=$blurOn)")
    }

    private fun resize(w: Int, h: Int) {
        val win = dialog?.window ?: return
        curW = w
        curH = h
        val lp = win.attributes
        lp.width = w
        lp.height = h
        win.attributes = lp
        // Nouveau fond a chaque taille : le flou reprend ainsi l'arrondi de la capsule.
        win.setBackgroundDrawable(Glass.island(density, w, h, blurOn))
    }

    private fun onTap() {
        val pi = currentIntent
        if (pi != null) {
            try {
                pi.send()
            } catch (e: Exception) {
                Log.w(TAG, "Ouverture impossible", e)
            }
        }
        main.removeCallbacks(collapseRunnable)
        collapse()
    }

    fun show(note: IslandNote) {
        if (dialog == null) return
        main.removeCallbacks(collapseRunnable)
        iconView.setImageDrawable(note.icon)
        titleView.text = note.title
        titleView.visibility = if (note.title.isEmpty()) View.GONE else View.VISIBLE
        textView.text = note.text
        textView.visibility = if (note.text.isEmpty()) View.GONE else View.VISIBLE
        currentIntent = note.intent

        content.visibility = View.VISIBLE
        content.animate().cancel()
        content.animate().setStartDelay(140).alpha(1f).setDuration(200).start()
        animateTo(expandedW, expandedH)
        main.postDelayed(collapseRunnable, 5000)
        Log.i(TAG, "Notification affichee : ${note.title}")
    }

    private fun collapse() {
        currentIntent = null
        content.animate().cancel()
        content.animate().setStartDelay(0).alpha(0f).setDuration(110)
            .withEndAction { content.visibility = View.INVISIBLE }.start()
        animateTo(collapsedW, collapsedH)
    }

    private fun animateTo(w: Int, h: Int) {
        val w0 = curW
        val h0 = curH
        anim?.cancel()
        anim = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 340
            interpolator = DecelerateInterpolator(1.7f)
            addUpdateListener {
                val f = it.animatedValue as Float
                resize((w0 + (w - w0) * f).toInt(), (h0 + (h - h0) * f).toInt())
            }
            start()
        }
        // Si les animations sont coupees sur le telephone, on applique directement la taille finale.
        if (!ValueAnimator.areAnimatorsEnabled()) resize(w, h)
    }

    /** Recree la fenetre (apres un changement de reglage, comme le flou). */
    fun rebuild() {
        anim?.cancel()
        main.removeCallbacks(collapseRunnable)
        try { dialog?.dismiss() } catch (e: Exception) { }
        dialog = null
        createIsland()
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        main.removeCallbacksAndMessages(null)
        anim?.cancel()
        try { dialog?.dismiss() } catch (e: Exception) { }
        dialog = null
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
