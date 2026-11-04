package com.liquidisland

import android.content.Context
import android.content.Intent
import android.graphics.Canvas
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.WindowManager

/**
 * Wave Theme sur telephone : un contour en verre liquide autour des applis du bas de l'ecran d'accueil
 * (le dock du lanceur), comme sur iPhone. Il ne capte AUCUN toucher : les icones restent cliquables
 * et le glissement vers le haut (accueil, applis recentes) marche comme d'habitude.
 * Visible seulement sur l'ecran d'accueil.
 */
class WaveDock(private val ctx: Context, private val wm: WindowManager, private val type: Int) {

    private val d = ctx.resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private val main = Handler(Looper.getMainLooper())

    private var view: Frame? = null
    private var lp: WindowManager.LayoutParams? = null
    private var allowed = true
    private var onHome = false
    private var shown = false
    private var homePkg: String? = null

    companion object { private const val TAG = "LiquidIsland" }

    fun attach() {
        if (view != null) return
        homePkg = try {
            @Suppress("DEPRECATION")
            ctx.packageManager.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), 0)?.activityInfo?.packageName
        } catch (e: Exception) { null }
        val v = Frame()
        val p = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT, dp(Prefs.dockHeight(ctx)), type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
            y = dp(Prefs.dockBottom(ctx))
            if (Build.VERSION.SDK_INT >= 30) fitInsetsTypes = 0
            windowAnimations = 0
        }
        v.alpha = 0f
        v.visibility = View.GONE
        try { wm.addView(v, p); view = v; lp = p } catch (e: Exception) { Log.w(TAG, "Dock : $e") }
    }

    fun detach() {
        view?.let { try { wm.removeView(it) } catch (e: Exception) { } }
        view = null
        shown = false
    }

    /** Reglages changes, plein ecran, rotation. */
    fun update(on: Boolean) {
        allowed = on
        val p = lp ?: return
        val h = dp(Prefs.dockHeight(ctx))
        val y = dp(Prefs.dockBottom(ctx))
        if (p.height != h || p.y != y) {
            p.height = h; p.y = y
            view?.let { try { wm.updateViewLayout(it, p) } catch (e: Exception) { } }
        }
        refresh()
    }

    /** Appli au premier plan : le contour n'apparait que sur l'ecran d'accueil. */
    fun onForeground(pkg: String) {
        if (pkg.contains("systemui") || pkg == ctx.packageName || pkg == "android") return
        onHome = pkg == homePkg || (homePkg == null && pkg.contains("launcher", ignoreCase = true))
        refresh()
    }

    private fun refresh() {
        val v = view ?: return
        val want = allowed && onHome && Prefs.dock(ctx)
        if (want == shown) return
        shown = want
        v.animate().cancel()
        if (want) {
            Log.i(TAG, "Dock : contour affiche sur l'accueil")
            v.visibility = View.VISIBLE
            v.scaleX = 0.92f; v.scaleY = 0.8f; v.alpha = 0f
            v.pivotY = v.height.toFloat()
            val s = Spring.POP
            v.animate().alpha(1f).scaleX(1f).scaleY(1f).setInterpolator(s).setDuration(s.durationMs).start()
        } else {
            v.animate().alpha(0f).setDuration(120).withEndAction { if (!shown) v.visibility = View.GONE }.start()
        }
    }

    /** Le contour : verre transparent avec reflets et bords bombes (ni flou ni voile sombre). */
    private inner class Frame : View(ctx) {
        private val glass = LiquidGlass(d).apply {
            clear = true
            dark = true
            tint = 0f
            fixedRadius = 32 * d
            lightX = -0.3f
            lightY = -0.6f
        }
        override fun onDraw(c: Canvas) {
            val m = 12 * d
            glass.build(m, 0f, width - m, height.toFloat())
            glass.drawBody(c)
            glass.drawEdges(c)
        }
    }
}
