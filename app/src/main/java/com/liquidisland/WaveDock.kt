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
        // Si finalement l'appli est restee ouverte (geste annule), on le voit et la zone revient
        main.removeCallbacks(recheck)
        main.postDelayed(recheck, 900)
        main.postDelayed(recheck, 2200)
    }

    private val recheck = Runnable {
        val pkg = try { IslandAccessibility.instance?.rootInActiveWindow?.packageName?.toString() } catch (e: Exception) { null }
        if (pkg != null && !inApp && !isHome(pkg)) onForeground(pkg)
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
                    if (!fired && dy > dp(6) && dy > abs(e.rawX - downX) * 0.7f) {
                        fired = true
                        Log.i(TAG, "Dock : glissement du bas")
                        show(e.rawX)
                    }
                }
                MotionEvent.ACTION_UP -> if (!fired && downY - e.rawY > dp(4)) { fired = true; show(e.rawX) }
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

    private val rowH get() = dp(CELL + 20)
    /** Fenetre « grande » (toute la largeur, jusqu'en bas) pendant les animations : la goutte a toute la place. */
    private val fullH get() = gestureHeight() + dp(6) + rowH + dp(60)
    private var dockLp: WindowManager.LayoutParams? = null

    fun show(fingerX: Float = ctx.resources.displayMetrics.widthPixels / 2f) {
        if (shown) return
        val favs = favorites()
        if (favs.isEmpty()) return
        shown = true
        Log.i(TAG, "Dock ouvert (${favs.size} favoris)")

        val row = LinearLayout(ctx).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(12), 0, dp(12), 0) }
        favs.forEachIndexed { i, p -> row.addView(icon(p, i)) }
        val dv = DockView(ctx, row)
        dv.addView(row, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, rowH, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply {
            bottomMargin = gestureHeight() + dp(6)
        })
        dock = dv
        val root = object : FrameLayout(ctx) {
            override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
                if (ev.actionMasked == MotionEvent.ACTION_OUTSIDE) {
                    // Toucher tout en bas pendant que le dock est la : c'est le 2e glissement (Android ferme l'appli)
                    val bottom = ev.rawY <= 0f || ev.rawY > ctx.resources.displayMetrics.heightPixels - gestureHeight() - dp(40)
                    if (bottom) { hide(pauseMs = 2500); leavingApp() } else hide()
                    return true
                }
                if (ev.actionMasked == MotionEvent.ACTION_DOWN) main.removeCallbacks(autoHide)
                val r = super.dispatchTouchEvent(ev)
                if (ev.actionMasked == MotionEvent.ACTION_UP) main.postDelayed({ hide() }, 150)
                return r
            }
        }
        root.clipChildren = false
        root.addView(dv, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        val lp = WindowManager.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, fullH, type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            y = 0
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(root, lp); dockRoot = root; dockLp = lp } catch (e: Exception) { shown = false; Log.w(TAG, "Dock impossible : $e"); return }
        root.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
        // Le dock est la : pendant ce temps le bas de l'ecran revient a Android (2e glissement = fermer l'appli)
        pause(3300)
        dv.post { dropIn(dv, fingerX) }
        main.removeCallbacks(autoHide)
        main.postDelayed(autoHide, 3000)
    }

    private val autoHide = Runnable { hide() }

    private fun lerp(a: Float, b: Float, t: Float) = a + (b - a) * t

    /**
     * La fenetre visible du dock ne change jamais de taille (sinon il sautait d'une image).
     * Une fois pose, une 2e fenetre invisible, juste sur le dock, recoit les touchers.
     */
    private var hitRoot: View? = null

    private fun windowSmall(dv: DockView) {
        removeHit()
        val row = dv.row
        val loc = IntArray(2)
        row.getLocationOnScreen(loc)
        var pressed: View? = null
        var longDone = false
        val longPress = Runnable { pressed?.let { longDone = true; it.performLongClick() } }
        val pad = dp(10)
        // La zone tactile recouvre exactement la rangee (avec une petite marge) : on cherche l'icone par sa position dans la rangee
        fun iconAt(x: Float, y: Float): View? {
            val rx = x - pad
            if (y < -pad || y > row.height + 3 * pad) return null
            var best: View? = null
            var bestD = Float.MAX_VALUE
            for (i in 0 until row.childCount) {
                val c = row.getChildAt(i)
                val cx = (c.left + c.right) / 2f
                val d = kotlin.math.abs(rx - cx)
                if (d < bestD) { bestD = d; best = c }
            }
            return if (best != null && bestD <= best.width * 0.75f) best else null
        }
        val hit = object : View(ctx) {
            override fun onTouchEvent(e: MotionEvent): Boolean {
                when (e.actionMasked) {
                    MotionEvent.ACTION_OUTSIDE -> {
                        // Toucher tout en bas pendant que le dock est la : c'est le 2e glissement (Android ferme l'appli)
                        val bottom = e.rawY <= 0f || e.rawY > ctx.resources.displayMetrics.heightPixels - gestureHeight() - dp(40)
                        if (bottom) { hide(pauseMs = 2500); leavingApp() } else hide()
                    }
                    MotionEvent.ACTION_DOWN -> {
                        main.removeCallbacks(autoHide)
                        longDone = false
                        pressed = iconAt(e.x, e.y)
                        pressed?.animate()?.scaleX(0.86f)?.scaleY(0.86f)?.setDuration(90)?.start()
                        main.postDelayed(longPress, 500)
                    }
                    MotionEvent.ACTION_MOVE -> {
                        if (pressed != null && iconAt(e.x, e.y) !== pressed) {
                            main.removeCallbacks(longPress)
                            pressed?.animate()?.scaleX(1f)?.scaleY(1f)?.setDuration(120)?.start()
                            pressed = null
                        }
                    }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                        main.removeCallbacks(longPress)
                        val p = pressed
                        pressed = null
                        if (p != null && !longDone && e.actionMasked == MotionEvent.ACTION_UP) p.performClick()
                        else {
                            p?.animate()?.scaleX(1f)?.scaleY(1f)?.setDuration(120)?.start()
                            main.postDelayed(autoHide, 1500)
                        }
                    }
                }
                return true
            }
        }
        val lp = WindowManager.LayoutParams(
            row.width + 2 * pad, row.height + 2 * pad, type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = loc[0] - pad; y = loc[1] - pad
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        try { wm.addView(hit, lp); hitRoot = hit } catch (e: Exception) { Log.w(TAG, "Zone tactile du dock impossible : $e") }
    }

    private fun removeHit() {
        val h = hitRoot ?: return
        hitRoot = null
        try { wm.removeView(h) } catch (e: Exception) { }
    }

    /**
     * Une vraie goutte ronde nait sous le doigt (tout en bas), monte en s'etirant, puis s'etale en dock
     * avec un rebond de gelee ; les icones sautent ensuite une a une a l'interieur.
     */
    private fun dropIn(dv: DockView, fingerX: Float) {
        val row = dv.row
        val w = row.width.toFloat().coerceAtLeast(1f)
        val h = row.height.toFloat().coerceAtLeast(1f)
        val screenW = ctx.resources.displayMetrics.widthPixels.toFloat()
        val dia = h * 0.62f
        val startDx = (fingerX - screenW / 2f).coerceIn(-screenW / 2f + dia, screenW / 2f - dia)
        val below = (gestureHeight() + dp(6)).toFloat() + dia * 0.5f
        dv.settled = false
        dv.offX = startDx; dv.offY = below; dv.sw = 0f; dv.sh = 0f
        dv.invalidate()
        // 1) la goutte nait et monte en s'etirant (plus haute que large)
        val a = android.animation.ValueAnimator.ofFloat(0f, 1f).setDuration(190)
        a.interpolator = android.view.animation.DecelerateInterpolator(1.5f)
        a.addUpdateListener {
            val t = it.animatedValue as Float
            dv.sw = dia * lerp(0.35f, 0.88f, t)
            dv.sh = dia * lerp(0.35f, 1.22f, t)
            dv.offY = lerp(below, -dp(8).toFloat(), t)
            dv.invalidate()
        }
        a.addListener(object : android.animation.AnimatorListenerAdapter() {
            override fun onAnimationEnd(an: android.animation.Animator) {
                if (dock !== dv || !shown) return
                // 2) elle s'etale jusqu'au centre en dock, deborde, puis la hauteur rebondit comme de la gelee
                val sW = Spring(0.52f, 0.5f)
                val sH = Spring(0.38f, 0.36f)
                val dur = maxOf(sW.durationMs, sH.durationMs).coerceAtMost(900)
                val b = android.animation.ValueAnimator.ofFloat(0f, 1f).setDuration(dur)
                b.interpolator = android.view.animation.LinearInterpolator()
                b.addUpdateListener {
                    val ms = it.currentPlayTime.toFloat()
                    val kw = sW.getInterpolation(ms / sW.durationMs)
                    val kh = sH.getInterpolation(ms / sH.durationMs)
                    dv.sw = lerp(dia * 0.88f, w, kw).coerceAtLeast(dia * 0.5f)
                    dv.offX = lerp(startDx, 0f, kw)
                    dv.offY = lerp(-dp(8).toFloat(), 0f, kw)
                    dv.sh = lerp(dia * 1.22f, h, kh)
                    dv.invalidate()
                }
                b.addListener(object : android.animation.AnimatorListenerAdapter() {
                    override fun onAnimationEnd(an: android.animation.Animator) {
                        if (dock !== dv || !shown) return
                        dv.settled = true
                        dv.invalidate()
                        windowSmall(dv)
                    }
                })
                dv.anim = b
                b.start()
                // Les icones sautent dans la goutte une fois qu'elle s'etale, l'une apres l'autre
                for (i in 0 until row.childCount) {
                    val ic = row.getChildAt(i)
                    val pop = Spring(0.42f, 0.4f)
                    ic.animate().scaleX(1f).scaleY(1f).alpha(1f).setStartDelay(110L + i * 60L)
                        .setInterpolator(pop).setDuration(pop.durationMs).start()
                }
            }
        })
        dv.anim = a
        a.start()
    }

    fun hide(animated: Boolean = true, pauseMs: Long = 350) {
        if (!shown) return
        shown = false
        main.removeCallbacks(autoHide)
        val root = dockRoot ?: return
        val dv = dock
        dockRoot = null
        dock = null
        val remove = { try { wm.removeView(root) } catch (e: Exception) { } }
        // Apres un 2e glissement (fermer l'appli) le bas reste a Android un moment ; sinon il revient tout de suite
        pausedUntil = 0L
        pause(pauseMs)
        if (!animated || dv == null) { removeHit(); remove(); return }
        val row = dv.row
        val w = row.width.toFloat().coerceAtLeast(1f)
        val h = row.height.toFloat().coerceAtLeast(1f)
        val dia = h * 0.62f
        removeHit()
        dv.anim?.cancel()
        if (!dv.settled) { dv.sw = dv.sw.coerceAtLeast(dia); dv.sh = dv.sh.coerceAtLeast(dia) } else { dv.sw = w; dv.sh = h; dv.offX = 0f; dv.offY = 0f }
        dv.settled = false
        // 1) les icones rentrent (petit elan puis disparaissent), de la derniere a la premiere
        val n = row.childCount
        for (i in 0 until n) {
            val ic = row.getChildAt(i)
            ic.animate().cancel()
            ic.animate().scaleX(0f).scaleY(0f).alpha(0f).setStartDelay((n - 1 - i) * 35L)
                .setInterpolator(android.view.animation.AnticipateInterpolator(2.2f)).setDuration(170).start()
        }
        val sw0 = dv.sw; val sh0 = dv.sh; val ox0 = dv.offX; val oy0 = dv.offY
        // 2) le dock se resserre en goutte ronde, avec un rebond
        val sC = Spring(0.5f, 0.42f)
        val c = android.animation.ValueAnimator.ofFloat(0f, 1f).setDuration(sC.durationMs.coerceAtMost(520))
        c.interpolator = android.view.animation.LinearInterpolator()
        c.startDelay = 110L + n * 25L
        c.addUpdateListener {
            val k = sC.getInterpolation(it.currentPlayTime.toFloat() / sC.durationMs)
            dv.sw = lerp(sw0, dia, k).coerceAtLeast(dia * 0.4f)
            dv.sh = lerp(sh0, dia, k).coerceAtLeast(dia * 0.4f)
            dv.offX = lerp(ox0, 0f, k); dv.offY = lerp(oy0, 0f, k)
            dv.invalidate()
        }
        c.addListener(object : android.animation.AnimatorListenerAdapter() {
            override fun onAnimationEnd(an: android.animation.Animator) {
                // 3) la goutte s'etire et tombe sous le bord de l'ecran
                val below = (gestureHeight() + dp(6)).toFloat() + dia * 1.6f
                val f = android.animation.ValueAnimator.ofFloat(0f, 1f).setDuration(260)
                f.interpolator = android.view.animation.AccelerateInterpolator(1.7f)
                f.addUpdateListener {
                    val t = it.animatedValue as Float
                    dv.sw = dia * lerp(1f, 0.7f, t)
                    dv.sh = dia * lerp(1f, 1.35f, t)
                    dv.offY = lerp(0f, below, t)
                    dv.alpha = 1f - t * t
                    dv.invalidate()
                }
                f.addListener(object : android.animation.AnimatorListenerAdapter() {
                    override fun onAnimationEnd(an: android.animation.Animator) { remove() }
                })
                dv.anim = f
                f.start()
            }
        })
        dv.anim = c
        c.start()
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
        iv.scaleX = 0f; iv.scaleY = 0f; iv.alpha = 0f   // elles sautent dans la goutte (dropIn)
        return iv.also { it.layoutParams = LinearLayout.LayoutParams(dp(CELL), dp(CELL)) }
    }

    /** Le verre du dock : une goutte / pilule de verre liquide transparent, dessinee a la forme voulue. */
    private inner class DockView(c: Context, val row: LinearLayout) : FrameLayout(c) {
        private val glass = LiquidGlass(d).apply {
            clear = true
            dark = true
            tint = 0f
            lightX = -0.3f
            lightY = -0.6f
        }
        /** Pose : la forme epouse la rangee d'icones. Sinon : goutte de taille [sw] x [sh], decalee de [offX]/[offY]. */
        var settled = false
        var anim: android.animation.Animator? = null
        var offX = 0f
        var offY = 0f
        var sw = 0f
        var sh = 0f
        private val clip = Path()
        init { setWillNotDraw(false); clipChildren = false }
        override fun dispatchDraw(canvas: Canvas) {
            val cx = (row.left + row.right) / 2f + if (settled) 0f else offX
            val bottom = row.bottom + if (settled) 0f else offY
            val wNow = if (settled) row.width.toFloat() else sw
            val hNow = if (settled) row.height.toFloat() else sh
            if (wNow < 1f || hNow < 1f) return
            val l = cx - wNow / 2f; val t = bottom - hNow; val r = cx + wNow / 2f
            glass.fixedRadius = minOf(wNow, hNow) / 2f   // toujours entierement rond
            glass.build(l, t, r, bottom)
            glass.drawBody(canvas)
            // Les icones n'apparaissent qu'a l'interieur de la goutte
            clip.reset()
            clip.addRoundRect(l, t, r, bottom, glass.fixedRadius, glass.fixedRadius, Path.Direction.CW)
            canvas.save()
            canvas.clipPath(clip)
            super.dispatchDraw(canvas)
            canvas.restore()
            glass.drawEdges(canvas)
        }
    }
}
