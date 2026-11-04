package com.liquidisland

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.MediaStore
import android.provider.Settings
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
import kotlin.math.abs

/**
 * Wave Theme sur telephone : un dock facon iPad, en verre liquide transparent.
 * Dans n'importe quelle appli, toucher (ou glisser un peu vers le haut) la barre tout en bas de l'ecran
 * le fait monter : applis favorites, puis les dernieres applis ouvertes. Un glissement plus long reste
 * le geste « accueil » d'Android.
 */
class WaveDock(private val ctx: Context, private val wm: WindowManager, private val type: Int) {

    private val d = ctx.resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private val main = Handler(Looper.getMainLooper())

    private var handle: View? = null
    private var handleLp: WindowManager.LayoutParams? = null
    private var dockRoot: FrameLayout? = null
    private var dock: DockView? = null
    private var shown = false
    private var enabledNow = false
    private val recents = ArrayList<String>()

    companion object {
        private const val TAG = "LiquidIsland"
        private const val ICON = 52
        private const val CELL = 64
    }

    // ================== Fenetres ==================

    /** Petite zone tactile invisible sur la barre « accueil » d'Android (au centre, pas sur les bords du retour). */
    fun attach() {
        if (handle != null) return
        val v = Handle()
        val lp = WindowManager.LayoutParams(
            (ctx.resources.displayMetrics.widthPixels * 0.46f).toInt(), gestureHeight(), type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(v, lp); handle = v; handleLp = lp } catch (e: Exception) { Log.w(TAG, "Dock : zone tactile impossible : $e") }
        update(true)
    }

    fun detach() {
        hide(animated = false)
        handle?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        handle = null
    }

    /** Actif seulement en navigation par gestes (avec 3 boutons, la barre du bas appartient aux boutons). */
    fun update(on: Boolean) {
        val lp = handleLp ?: return
        val want = on && Prefs.dock(ctx) && gestureNav()
        if (want == enabledNow && lp.height == gestureHeight()) return
        enabledNow = want
        val noTouch = WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        lp.flags = if (want) lp.flags and noTouch.inv() else lp.flags or noTouch
        lp.height = gestureHeight()
        lp.width = (ctx.resources.displayMetrics.widthPixels * 0.46f).toInt()
        handle?.let { try { wm.updateViewLayout(it, lp) } catch (e: Exception) { } }
        if (!want) hide(animated = false)
    }

    private fun gestureNav(): Boolean = try {
        Settings.Secure.getInt(ctx.contentResolver, "navigation_mode", 0) == 2
    } catch (e: Exception) { false }

    private fun gestureHeight(): Int {
        val id = ctx.resources.getIdentifier("navigation_bar_gesture_height", "dimen", "android")
        val h = if (id > 0) ctx.resources.getDimensionPixelSize(id) else 0
        return maxOf(h, dp(18))
    }

    /** Derniere appli au premier plan (pour la partie « recentes » du dock). */
    fun onForeground(pkg: String) {
        if (pkg == ctx.packageName || pkg.contains("systemui") || pkg.contains("launcher") || pkg == "android") return
        if (ctx.packageManager.getLaunchIntentForPackage(pkg) == null) return
        recents.remove(pkg)
        recents.add(0, pkg)
        while (recents.size > 8) recents.removeAt(recents.size - 1)
        if (shown) hide()
    }

    @SuppressLint("ViewConstructor", "ClickableViewAccessibility")
    private inner class Handle : View(ctx) {
        private val slop = ViewConfiguration.get(ctx).scaledTouchSlop
        private var downY = 0f
        private var downAt = 0L
        private var fired = false
        override fun onTouchEvent(e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downY = e.rawY; downAt = SystemClock.uptimeMillis(); fired = false }
                MotionEvent.ACTION_MOVE -> if (!fired && downY - e.rawY > slop) { fired = true; show() }
                MotionEvent.ACTION_UP -> {
                    // Un simple toucher sur la barre du bas (Android n'en fait rien) : le dock monte
                    if (!fired && SystemClock.uptimeMillis() - downAt < 600) { fired = true; show() }
                }
                // Glissement long : c'est le geste « accueil » d'Android, on le laisse faire
                MotionEvent.ACTION_CANCEL -> if (fired) hide()
            }
            return true
        }
    }

    // ================== Le dock ==================

    private fun favorites(): List<String> {
        val saved = Prefs.dockApps(ctx)
        if (saved.isNotEmpty()) return saved.filter { ctx.packageManager.getLaunchIntentForPackage(it) != null }
        // Par defaut : telephone, messages, navigateur, appareil photo
        val pm = ctx.packageManager
        fun pkgOf(i: Intent) = try {
            @Suppress("DEPRECATION")
            pm.resolveActivity(i, 0)?.activityInfo?.packageName?.takeIf { it != "android" && pm.getLaunchIntentForPackage(it) != null }
        } catch (e: Exception) { null }
        return listOfNotNull(
            pkgOf(Intent(Intent.ACTION_DIAL)),
            pkgOf(Intent(Intent.ACTION_SENDTO, Uri.parse("smsto:"))),
            pkgOf(Intent(Intent.ACTION_VIEW, Uri.parse("https://example.com"))),
            pkgOf(Intent(MediaStore.INTENT_ACTION_STILL_IMAGE_CAMERA)),
        ).distinct()
    }

    fun show() {
        if (shown) return
        val favs = favorites()
        val rec = recents.filter { it !in favs }.take(3)
        if (favs.isEmpty() && rec.isEmpty()) return
        shown = true
        handle?.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
        Log.i(TAG, "Dock ouvert (${favs.size} favoris, ${rec.size} recentes)")

        val row = LinearLayout(ctx).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(10), 0, dp(10), 0) }
        var i = 0
        favs.forEach { row.addView(icon(it, i++)) }
        if (rec.isNotEmpty()) {
            row.addView(View(ctx).apply { setBackgroundColor(Color.argb(110, 255, 255, 255)) },
                LinearLayout.LayoutParams(dp(1), dp(40)).apply { leftMargin = dp(8); rightMargin = dp(8) })
            rec.forEach { row.addView(icon(it, i++)) }
        }
        val dv = DockView(ctx).apply { addView(row, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT)) }
        dock = dv
        val root = object : FrameLayout(ctx) {
            override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
                if (ev.actionMasked == MotionEvent.ACTION_OUTSIDE) { hide(); return true }
                return super.dispatchTouchEvent(ev)
            }
        }
        root.clipChildren = false
        root.addView(dv, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(CELL + 20), Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply {
            bottomMargin = gestureHeight() + dp(6)
        })
        val lp = WindowManager.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, dp(CELL + 20) + gestureHeight() + dp(16), type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(root, lp); dockRoot = root } catch (e: Exception) { shown = false; Log.w(TAG, "Dock impossible : $e"); return }

        // Il monte depuis la barre du bas en s'etirant, avec un rebond (comme une goutte)
        dv.pivotY = dp(CELL + 20).toFloat()
        dv.translationY = dp(60).toFloat()
        dv.scaleX = 0.4f
        dv.scaleY = 0.3f
        dv.alpha = 0f
        val s = Spring.OPEN
        dv.animate().translationY(0f).scaleX(1f).scaleY(1f).setInterpolator(s).setDuration(s.durationMs).start()
        dv.animate().alpha(1f).setDuration(140).start()
        main.removeCallbacks(autoHide)
        main.postDelayed(autoHide, 6000)
    }

    private val autoHide = Runnable { hide() }

    fun hide(animated: Boolean = true) {
        if (!shown) return
        shown = false
        main.removeCallbacks(autoHide)
        val root = dockRoot ?: return
        val dv = dock
        dockRoot = null
        dock = null
        val remove = { try { wm.removeView(root) } catch (e: Exception) { } }
        if (!animated || dv == null) { remove(); return }
        val s = Spring.CLOSE
        dv.animate().translationY(dp(70).toFloat()).scaleX(0.5f).scaleY(0.4f).alpha(0f).setInterpolator(s)
            .setDuration((s.durationMs * 0.6).toLong()).withEndAction { remove() }.start()
    }

    private fun icon(pkg: String, index: Int): View {
        val pm = ctx.packageManager
        val iv = ImageView(ctx).apply {
            try { setImageDrawable(pm.getApplicationIcon(pkg)) } catch (e: Exception) { }
            setPadding(dp(6), dp(6), dp(6), dp(6))
            isClickable = true
            setOnClickListener {
                performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
                animate().scaleX(0.82f).scaleY(0.82f).setDuration(80).withEndAction {
                    val s = Spring.POP
                    animate().scaleX(1f).scaleY(1f).setInterpolator(s).setDuration(s.durationMs).start()
                }.start()
                pm.getLaunchIntentForPackage(pkg)?.let {
                    it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    try { ctx.startActivity(it) } catch (e: Exception) { }
                }
                main.postDelayed({ hide() }, 120)
            }
            setOnLongClickListener {
                // Appui long : ajoute aux favoris (ou retire)
                val favs = favorites().toMutableList()
                if (pkg in favs) favs.remove(pkg) else if (favs.size < 6) favs.add(pkg)
                Prefs.setDockApps(ctx, favs)
                performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                hide()
                true
            }
        }
        // Chaque icone arrive avec un petit rebond, l'une apres l'autre
        iv.scaleX = 0.4f; iv.scaleY = 0.4f
        val s = Spring.POP
        iv.animate().scaleX(1f).scaleY(1f).setStartDelay(60L + index * 30L).setInterpolator(s).setDuration(s.durationMs).start()
        return iv.also { it.layoutParams = LinearLayout.LayoutParams(dp(CELL), dp(CELL)) }
    }

    /** Le verre du dock : transparent, reflets et bords bombes comme l'ilot (ni flou ni voile sombre). */
    private inner class DockView(c: Context) : FrameLayout(c) {
        private val glass = LiquidGlass(d).apply {
            clear = true
            dark = true
            tint = 0f
            fixedRadius = 30 * d
            lightX = -0.3f
            lightY = -0.6f
        }
        init { setWillNotDraw(false); clipChildren = false }
        override fun dispatchDraw(canvas: Canvas) {
            glass.build(0f, 0f, width.toFloat(), height.toFloat())
            glass.drawBody(canvas)
            super.dispatchDraw(canvas)
            glass.drawEdges(canvas)
        }
    }
}
