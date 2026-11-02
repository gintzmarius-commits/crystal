package com.liquidisland

import android.animation.Animator
import android.animation.AnimatorListenerAdapter
import android.animation.ValueAnimator
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.LayerDrawable
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.view.animation.DecelerateInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.app.PendingIntent

class IslandService : Service() {

    private lateinit var wm: WindowManager
    private val main = Handler(Looper.getMainLooper())

    private var container: FrameLayout? = null
    private lateinit var pill: FrameLayout
    private lateinit var content: LinearLayout
    private lateinit var iconView: ImageView
    private lateinit var titleView: TextView
    private lateinit var textView: TextView

    private var currentIntent: PendingIntent? = null
    private var anim: ValueAnimator? = null
    private var animGen = 0
    private val collapseRunnable = Runnable { collapse() }

    private fun dp(v: Float) = (v * resources.displayMetrics.density).toInt()

    private val collapsedW get() = dp(116f)
    private val collapsedH get() = dp(34f)
    private val expandedH get() = dp(78f)
    private val expandedW get() = minOf(dp(360f), resources.displayMetrics.widthPixels - dp(24f))

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onCreate() {
        super.onCreate()
        wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager

        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Liquid Island", NotificationManager.IMPORTANCE_LOW)
        )
        val notif = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_info_details)
            .setContentTitle("Liquid Island actif")
            .setContentText("L'ilot est affiche.")
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(1, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(1, notif)
        }

        if (!Settings.canDrawOverlays(this)) {
            stopSelf()
            return
        }
        addIsland()
        IslandBus.listener = { note -> main.post { show(note) } }
    }

    private fun addIsland() {
        iconView = ImageView(this).apply {
            layoutParams = LinearLayout.LayoutParams(dp(30f), dp(30f))
        }
        titleView = TextView(this).apply {
            setTextColor(INK)
            textSize = 14f
            setTypeface(typeface, Typeface.BOLD)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }
        textView = TextView(this).apply {
            setTextColor(INK_SOFT)
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
            setPadding(dp(16f), 0, dp(16f), 0)
            addView(iconView)
            addView(column)
            alpha = 0f
            visibility = View.GONE
        }
        pill = FrameLayout(this).apply {
            background = glass()
            addView(
                content,
                FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            )
            setOnClickListener {
                val pi = currentIntent
                if (pi != null) {
                    try {
                        pi.send()
                    } catch (e: Exception) {
                        // ignore
                    }
                }
                main.removeCallbacks(collapseRunnable)
                collapse()
            }
        }
        val root = FrameLayout(this)
        root.addView(pill, FrameLayout.LayoutParams(collapsedW, collapsedH, Gravity.CENTER_HORIZONTAL))
        container = root

        val params = WindowManager.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            y = dp(8f)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && Prefs.blur(this@IslandService)) {
                flags = flags or WindowManager.LayoutParams.FLAG_BLUR_BEHIND
                blurBehindRadius = dp(20f)
            }
        }
        wm.addView(root, params)
    }

    private fun glass(): Drawable {
        val radius = dp(40f).toFloat()
        val fill = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            intArrayOf(
                Color.argb(150, 255, 255, 255),
                Color.argb(70, 220, 232, 255),
                Color.argb(120, 255, 255, 255)
            )
        ).apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = radius
            setStroke(dp(1f), Color.argb(170, 255, 255, 255))
        }
        val shine = GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(Color.argb(95, 255, 255, 255), Color.argb(0, 255, 255, 255))
        ).apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = radius
        }
        return LayerDrawable(arrayOf(fill, shine)).apply {
            setLayerInset(1, dp(3f), dp(2f), dp(3f), dp(14f))
        }
    }

    private fun show(note: IslandNote) {
        if (container == null) return
        main.removeCallbacks(collapseRunnable)
        iconView.setImageDrawable(note.icon)
        titleView.text = note.title
        titleView.visibility = if (note.title.isEmpty()) View.GONE else View.VISIBLE
        textView.text = note.text
        textView.visibility = if (note.text.isEmpty()) View.GONE else View.VISIBLE
        currentIntent = note.intent

        content.visibility = View.VISIBLE
        content.animate().setStartDelay(120).alpha(1f).setDuration(220).start()
        animateTo(expandedW, expandedH)
        main.postDelayed(collapseRunnable, 5000)
    }

    private fun collapse() {
        currentIntent = null
        content.animate().setStartDelay(0).alpha(0f).setDuration(120)
            .withEndAction { content.visibility = View.GONE }.start()
        animateTo(collapsedW, collapsedH)
    }

    private fun animateTo(w: Int, h: Int) {
        val lp = pill.layoutParams
        val w0 = lp.width
        val h0 = lp.height
        anim?.cancel()
        animGen++
        anim = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 320
            interpolator = DecelerateInterpolator(1.6f)
            addUpdateListener {
                val f = it.animatedValue as Float
                lp.width = (w0 + (w - w0) * f).toInt()
                lp.height = (h0 + (h - h0) * f).toInt()
                pill.layoutParams = lp
            }
            addListener(object : AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: Animator) {
                    // rien : la taille finale est deja appliquee par le dernier pas
                }
            })
            start()
        }
    }

    override fun onDestroy() {
        IslandBus.listener = null
        main.removeCallbacksAndMessages(null)
        anim?.cancel()
        container?.let {
            try {
                wm.removeView(it)
            } catch (e: Exception) {
                // ignore
            }
        }
        container = null
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "island"
        private val INK = Color.rgb(24, 26, 36)
        private val INK_SOFT = Color.rgb(60, 64, 80)
    }
}
