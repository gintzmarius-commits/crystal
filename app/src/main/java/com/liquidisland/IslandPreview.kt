package com.liquidisland

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.view.View

/** Apercu du haut de l'ecran avec l'ilot, a l'echelle, qui suit les reglages. */
class IslandPreview(ctx: Context) : View(ctx) {
    private val glass = LiquidGlass(ctx.resources.displayMetrics.density)
    private val wall = Paint(Paint.ANTI_ALIAS_FLAG)
    private val halo = Paint(Paint.ANTI_ALIAS_FLAG)
    private val clip = Path()
    private val box = RectF()

    var notch = false
    var widthDp = 120
    var heightDp = 34
    var offsetDp = 8
    var expanded = false

    fun update() = invalidate()

    override fun onDraw(c: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        val d = resources.displayMetrics.density
        val r = 22 * d
        box.set(0f, 0f, w, h)
        clip.reset()
        clip.addRoundRect(box, r, r, Path.Direction.CW)
        c.save()
        c.clipPath(clip)
        // « Fond d'ecran » colore pour voir le verre
        wall.shader = LinearGradient(0f, 0f, w, h, intArrayOf(Color.rgb(48, 64, 140), Color.rgb(130, 80, 170), Color.rgb(230, 140, 120)), null, Shader.TileMode.CLAMP)
        c.drawRect(box, wall)
        halo.shader = RadialGradient(w * 0.75f, h * 0.2f, w * 0.4f, Color.argb(160, 255, 220, 140), Color.argb(0, 255, 220, 140), Shader.TileMode.CLAMP)
        c.drawRect(box, halo)

        // Echelle : la largeur de l'apercu represente la largeur de l'ecran
        val screenWDp = resources.displayMetrics.widthPixels / d
        val s = w / screenWDp
        val pw: Float
        val ph: Float
        if (expanded) {
            // Meme calcul que l'ilot : le contenu commence sous la barre d'etat
            val id = resources.getIdentifier("status_bar_height", "dimen", "android")
            val sb = if (id > 0) resources.getDimensionPixelSize(id) / d else 28f
            val cTop = if (notch) maxOf(heightDp.toFloat(), sb) + 2f else maxOf(6f, sb - offsetDp + 2f)
            pw = minOf(360f, screenWDp - 20f) * s
            ph = (cTop + 64f + 8f) * s
        } else {
            pw = widthDp * s
            ph = heightDp * s
        }
        val top = if (notch) 0f else offsetDp * s
        val l = (w - pw) / 2f
        glass.notch = notch
        glass.dark = Prefs.isDark(context)
        glass.blurred = Prefs.blur(context)
        glass.tint = if (expanded) 1f else 0f
        glass.build(l, top, l + pw, top + ph)
        glass.drawBody(c)
        glass.drawEdges(c)
        c.restore()
    }
}
