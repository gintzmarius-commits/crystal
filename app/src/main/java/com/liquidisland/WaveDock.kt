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

    private var dockRoot: FrameLayout? = null
    private var dock: DockView? = null
    private var shown = false
    private var allowed = true
    private var inApp = false
    private var homePkg: String? = null
    private var lastPkg: String? = null
    private val recents = ArrayList<String>()
    private var handle: View? = null
    private var handleLp: WindowManager.LayoutParams? = null
    private var handleOn = false
    /** Pendant que le dock est affiche (et un instant apres), le bas de l'ecran est laisse a Android. */
    private var pausedUntil = 0L

    companion object {
        private const val TAG = "LiquidIsland"
        private const val CELL = 64
        const val MAX = 4

        /** Applis du dock choisies (sinon : telephone, messages, navigateur, appareil photo), 4 au maximum. */
        fun apps(c: Context): List<String> {
            val saved = Prefs.dockApps(c)
            val list = if (saved.isNotEmpty()) saved else defaults(c)
            return list.filter { c.packageManager.getLaunchIntentForPackage(it) != null }.distinct().take(MAX)
        }

        fun defaults(c: Context): List<String> {
            val pm = c.packageManager
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

        private val HOMES = listOf("com.miui.home", "com.mi.android.globallauncher", "com.sec.android.app.launcher",
            "com.google.android.apps.nexuslauncher", "com.android.launcher3", "com.oppo.launcher", "com.huawei.android.launcher")
    }

    // ================== Le geste du bas ==================
    //
    // Dans une appli : une fine zone tout en bas de l'ecran, posee par-dessus la petite barre d'Android.
    // Glisser vers le haut dessus : le dock monte et reste 3 secondes (ou jusqu'au prochain toucher).
    // Pendant ce temps la zone est retiree : le glissement suivant est celui d'Android (fermer l'appli,
    // barre des taches...). Wave Island ne ferme jamais l'appli elle-meme.

    fun attach() {
        homePkg = try {
            @Suppress("DEPRECATION")
            ctx.packageManager.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), 0)?.activityInfo?.packageName
                ?.takeIf { it != "android" }
        } catch (e: Exception) { null }
        if (handle != null) return
        val v = Handle()
        val lp = WindowManager.LayoutParams(
            (ctx.resources.displayMetrics.widthPixels * 0.7f).toInt(), gestureHeight(), type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(v, lp); handle = v; handleLp = lp } catch (e: Exception) { Log.w(TAG, "Dock : zone du bas impossible : $e") }
        Log.i(TAG, "Dock : zone du bas de ${gestureHeight()} px (accueil = $homePkg)")
        refresh()
    }

    fun detach() {
        hide(animated = false)
        handle?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        handle = null
        handleOn = false
    }

    /** Reglages changes, plein ecran, rotation. */
    fun update(on: Boolean) { allowed = on; refresh() }

    private fun isHome(pkg: String) = pkg == homePkg || pkg in HOMES || pkg.contains("launcher", ignoreCase = true)

    /** Appli au premier plan : la zone n'existe que dans une appli (jamais sur l'ecran d'accueil). */
    fun onForeground(pkg: String) {
        if (pkg.contains("systemui") || pkg == "android") return
        val ime = pkg.contains("inputmethod") || pkg.contains("keyboard") || pkg.contains(".ime")
        if (ime) return
        val home = isHome(pkg)
        if (!home) {
            if (pkg != ctx.packageName && ctx.packageManager.getLaunchIntentForPackage(pkg) == null) return
            if (pkg != ctx.packageName) { recents.remove(pkg); recents.add(0, pkg); while (recents.size > 8) recents.removeAt(recents.size - 1) }
        }
        if (inApp != !home) Log.i(TAG, "Dock : ${if (home) "accueil" else "appli ouverte ($pkg)"}")
        inApp = !home
        // Le dock lui-meme (fenetre de Wave Island) declenche aussi cet evenement : on ne le ferme
        // que si on change vraiment d'appli
        if (shown && pkg != lastPkg && pkg != ctx.packageName) hide()
        lastPkg = pkg
        refresh()
    }

    private fun gestureNav(): Boolean = try {
        Settings.Secure.getInt(ctx.contentResolver, "navigation_mode", 0) == 2
    } catch (e: Exception) { false }

    /** Hauteur de la petite barre d'accueil d'Android (on se pose exactement dessus). */
    private fun gestureHeight(): Int {
        val id = ctx.resources.getIdentifier("navigation_bar_height", "dimen", "android")
        val h = if (id > 0) ctx.resources.getDimensionPixelSize(id) else 0
        return maxOf(h, dp(16)).coerceAtMost(dp(34))
    }

    private val refreshLater = Runnable { refresh() }

    private fun refresh() {
        val lp = handleLp ?: return
        val now = SystemClock.uptimeMillis()
        val want = allowed && inApp && Prefs.dock(ctx) && gestureNav() && !shown && now >= pausedUntil
        if (!(allowed && inApp && Prefs.dock(ctx)) && shown) hide(animated = false)
        if (want == handleOn && lp.height == gestureHeight()) return
        handleOn = want
        val noTouch = WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        lp.flags = if (want) lp.flags and noTouch.inv() else lp.flags or noTouch
        lp.height = gestureHeight()
        handle?.let { try { wm.updateViewLayout(it, lp) } catch (e: Exception) { } }
    }

    /**
     * HyperOS ne previent pas toujours quand on revient a l'accueil par le geste : on considere qu'on y est
     * jusqu'a ce qu'une appli repasse au premier plan (sinon le dock s'ouvrait sur l'ecran d'accueil).
     */
    private fun leavingApp() {
        inApp = false
        lastPkg = homePkg
        refresh()
    }

    /** Laisse le bas de l'ecran a Android pendant [ms] (2e glissement = fermer l'appli, barre des taches...). */
    private fun pause(ms: Long) {
        pausedUntil = SystemClock.uptimeMillis() + ms
        refresh()
        main.removeCallbacks(refreshLater)
        main.postDelayed(refreshLater, ms + 20)
    }

    @SuppressLint("ViewConstructor", "ClickableViewAccessibility")
    private inner class Handle : View(ctx) {
        private val slop = ViewConfiguration.get(ctx).scaledTouchSlop
        private var downX = 0f
        private var downY = 0f
        private var fired = false
        override fun onTouchEvent(e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downX = e.rawX; downY = e.rawY; fired = false }
                MotionEvent.ACTION_MOVE -> {
                    val dy = downY - e.rawY
                    if (!fired && dy > slop && dy > abs(e.rawX - downX)) {
                        fired = true
                        Log.i(TAG, "Dock : glissement du bas")
                        show()
                    }
                }
                MotionEvent.ACTION_UP -> if (!fired && downY - e.rawY > slop) { fired = true; show() }
                MotionEvent.ACTION_CANCEL -> if (!fired) {
                    // Android a pris le geste : l'appli se ferme, on part du principe qu'on revient a l'accueil
                    Log.i(TAG, "Dock : glissement pris par Android")
                    leavingApp()
                }
            }
            return true
        }
    }

    // ================== Le dock ==================

    /** Les applis du dock : celles choisies dans Wave Island (4 au maximum). */
    private fun favorites(): List<String> = WaveDock.apps(ctx)

    fun show() {
        if (shown) return
        val favs = favorites()
        val rec = emptyList<String>()   // seulement les applis choisies (pas une barre des taches)
        if (favs.isEmpty() && rec.isEmpty()) return
        shown = true
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
                // Toucher ailleurs (ou glisser depuis le bas : Android s'en charge) : le dock redescend
                if (ev.actionMasked == MotionEvent.ACTION_OUTSIDE) {
                    // Toucher tout en bas pendant que le dock est la : c'est le 2e glissement (Android ferme l'appli)
                    val bottom = ev.rawY <= 0f || ev.rawY > ctx.resources.displayMetrics.heightPixels - gestureHeight() - dp(40)
                    hide()
                    if (bottom) leavingApp()
                    return true
                }
                if (ev.actionMasked == MotionEvent.ACTION_DOWN) main.removeCallbacks(autoHide)
                val r = super.dispatchTouchEvent(ev)
                // Toucher le dock (pas une icone) : il redescend aussi
                if (ev.actionMasked == MotionEvent.ACTION_UP) main.postDelayed({ hide() }, 150)
                return r
            }
        }
        root.clipChildren = false
        root.addView(dv, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(CELL + 20), Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply {
            bottomMargin = dp(2)
        })
        // La fenetre du dock s'arrete AU-DESSUS de la petite barre : le bas de l'ecran reste a Android
        val lp = WindowManager.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, dp(CELL + 20) + dp(12), type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            y = gestureHeight() + dp(4)
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(root, lp); dockRoot = root } catch (e: Exception) { shown = false; Log.w(TAG, "Dock impossible : $e"); return }
        root.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
        // Le dock est la : pendant 3 s le bas de l'ecran revient a Android (2e glissement = fermer l'appli)
        pause(3300)

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
        main.postDelayed(autoHide, 3000)
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
        // La zone du bas revient un peu apres (le glissement d'Android en cours n'est pas gene)
        // Le bas de l'ecran reste a Android encore 2,5 s : un 2e glissement ferme l'appli au lieu de rouvrir le dock
        pause(2500)
        if (!animated || dv == null) { remove(); return }
        // Goutte : le dock s'etire vers le bas, s'affine, puis tombe sous le bord de l'ecran
        dv.animate().scaleY(1.18f).scaleX(0.9f).setDuration(90).withEndAction {
            val s = Spring.CLOSE
            dv.animate().translationY(dp(110).toFloat()).scaleX(0.55f).scaleY(0.7f).alpha(0f).setInterpolator(s)
                .setDuration((s.durationMs * 0.55).toLong()).withEndAction { remove() }.start()
        }.start()
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
                if (pkg in favs) favs.remove(pkg) else if (favs.size < MAX) favs.add(pkg)
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
