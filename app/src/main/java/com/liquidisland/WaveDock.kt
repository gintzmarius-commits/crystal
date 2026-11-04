package com.liquidisland

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Path
import android.graphics.PixelFormat
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
 * Wave Theme sur telephone : le dock facon iPad, en verre liquide transparent.
 * Seulement quand une appli est ouverte : un premier glissement vers le haut depuis le bas de l'ecran
 * fait monter le dock (applis favorites + recentes) ; un deuxieme glissement quitte l'appli (accueil).
 *
 * Android garde pour lui les glissements qui partent tout au bord (geste accueil) : la zone qui ouvre
 * le dock est donc juste au-dessus de la petite barre d'accueil. Les simples touchers et les glissements
 * horizontaux qui y tombent sont rejoues aussitot pour l'appli en dessous.
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
    private var allowed = true
    private var inApp = false
    private var touchable = false
    private var homePkg: String? = null
    private var lastPkg: String? = null
    private val recents = ArrayList<String>()

    companion object {
        private const val TAG = "LiquidIsland"
        private const val CELL = 64
        private const val BAND = 22
    }

    // ================== Zone qui ouvre le dock ==================

    fun attach() {
        if (handle != null) return
        homePkg = try {
            @Suppress("DEPRECATION")
            ctx.packageManager.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), 0)?.activityInfo?.packageName
        } catch (e: Exception) { null }
        val v = Handle()
        val lp = WindowManager.LayoutParams(
            (ctx.resources.displayMetrics.widthPixels * 0.62f).toInt(), dp(BAND), type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            y = gestureHeight()
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(v, lp); handle = v; handleLp = lp } catch (e: Exception) { Log.w(TAG, "Dock : zone tactile impossible : $e") }
        Log.i(TAG, "Dock : zone de ${dp(BAND)} px a ${gestureHeight()} px du bas")
        refresh()
    }

    fun detach() {
        hide(animated = false)
        handle?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        handle = null
    }

    /** Reglages changes, plein ecran, rotation. */
    fun update(on: Boolean) { allowed = on; refresh() }

    /** Appli au premier plan : la zone n'existe que dans une appli (pas sur l'ecran d'accueil). */
    fun onForeground(pkg: String) {
        if (pkg.contains("systemui") || pkg == "android") return
        val home = pkg == homePkg || (homePkg == null && pkg.contains("launcher", ignoreCase = true))
        if (!home) {
            if (ctx.packageManager.getLaunchIntentForPackage(pkg) == null && !pkg.contains("inputmethod") && !pkg.contains("keyboard")) return
            if (!pkg.contains("inputmethod") && !pkg.contains("keyboard")) {
                recents.remove(pkg); recents.add(0, pkg)
                while (recents.size > 8) recents.removeAt(recents.size - 1)
            }
        }
        if (inApp != !home) Log.i(TAG, "Dock : ${if (home) "accueil" else "appli ouverte ($pkg)"}")
        inApp = !home
        // Le dock lui-meme (fenetre de Wave Island) declenche aussi cet evenement : on ne le ferme
        // que si on change vraiment d'appli
        if (shown && pkg != lastPkg) hide()
        lastPkg = pkg
        refresh()
    }

    private fun gestureNav(): Boolean = try {
        Settings.Secure.getInt(ctx.contentResolver, "navigation_mode", 0) == 2
    } catch (e: Exception) { false }

    private fun gestureHeight(): Int {
        // Hauteur de la petite barre d'accueil (la ou Android garde le geste pour lui)
        val id = ctx.resources.getIdentifier("navigation_bar_height", "dimen", "android")
        val h = if (id > 0) ctx.resources.getDimensionPixelSize(id) else 0
        return maxOf(h, dp(14)).coerceAtMost(dp(40))
    }

    private fun refresh() {
        if (handleLp == null) return
        val want = allowed && inApp && Prefs.dock(ctx) && gestureNav() && IslandAccessibility.instance != null
        setTouchable(want)
        if (!want && shown) hide(animated = false)
    }

    private fun setTouchable(on: Boolean) {
        val lp = handleLp ?: return
        if (on == touchable && lp.y == gestureHeight()) return
        touchable = on
        val noTouch = WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        lp.flags = if (on) lp.flags and noTouch.inv() else lp.flags or noTouch
        lp.y = gestureHeight()
        handle?.let { try { wm.updateViewLayout(it, lp) } catch (e: Exception) { } }
    }

    /** Deuxieme glissement : on quitte l'appli. */
    private fun goHome() {
        hide()
        Log.i(TAG, "Dock : deuxieme glissement, retour a l'accueil")
        IslandAccessibility.instance?.performGlobalAction(AccessibilityService.GLOBAL_ACTION_HOME)
    }

    /**
     * Ce n'etait pas pour nous (toucher, glissement de cote) : on rejoue le geste pour l'appli en dessous.
     * La zone devient transparente aux touchers le temps du geste rejoue.
     */
    private fun passThrough(points: List<FloatArray>, durationMs: Long) {
        val acc = IslandAccessibility.instance ?: return
        if (Build.VERSION.SDK_INT < 24 || points.isEmpty()) return
        setTouchable(false)
        val path = Path().apply {
            moveTo(points[0][0], points[0][1])
            for (i in 1 until points.size) lineTo(points[i][0], points[i][1])
        }
        val g = GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, durationMs.coerceIn(1, 1500)))
            .build()
        val back = Runnable { refresh() }
        main.postDelayed(back, durationMs + 400)
        try {
            acc.dispatchGesture(g, object : AccessibilityService.GestureResultCallback() {
                override fun onCompleted(gestureDescription: GestureDescription?) { main.removeCallbacks(back); main.postDelayed(back, 30) }
                override fun onCancelled(gestureDescription: GestureDescription?) { main.removeCallbacks(back); main.postDelayed(back, 30) }
            }, main)
        } catch (e: Exception) { main.removeCallbacks(back); refresh() }
    }

    @SuppressLint("ViewConstructor", "ClickableViewAccessibility")
    private inner class Handle : View(ctx) {
        private val slop = ViewConfiguration.get(ctx).scaledTouchSlop
        private var downX = 0f
        private var downY = 0f
        private var downAt = 0L
        private var fired = false
        private val pts = ArrayList<FloatArray>()
        override fun onTouchEvent(e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = e.rawX; downY = e.rawY; downAt = SystemClock.uptimeMillis(); fired = false
                    pts.clear(); pts.add(floatArrayOf(e.rawX, e.rawY))
                }
                MotionEvent.ACTION_MOVE -> {
                    pts.add(floatArrayOf(e.rawX, e.rawY))
                    val dy = downY - e.rawY
                    if (!fired && dy > dp(18) && dy > abs(e.rawX - downX)) {
                        fired = true
                        // 1er glissement : le dock monte ; 2e (dock deja la) : on quitte l'appli
                        if (shown) goHome() else show()
                    }
                }
                MotionEvent.ACTION_UP -> {
                    pts.add(floatArrayOf(e.rawX, e.rawY))
                    if (!fired) passThrough(pts.toList(), SystemClock.uptimeMillis() - downAt)
                }
                MotionEvent.ACTION_CANCEL -> fired = true
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
        handle?.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
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
            private var sy = 0f
            override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
                if (ev.actionMasked == MotionEvent.ACTION_OUTSIDE) { hide(); return true }
                // Deuxieme glissement vers le haut (sur le dock) : on quitte l'appli
                if (ev.actionMasked == MotionEvent.ACTION_DOWN) sy = ev.rawY
                if (ev.actionMasked == MotionEvent.ACTION_MOVE && sy - ev.rawY > dp(40)) { goHome(); return true }
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
