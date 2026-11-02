package com.liquidisland

import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import kotlin.math.max
import kotlin.math.min

/**
 * Verre liquide dessine a la main, sans flou :
 * corps presque transparent, epaisseur visible sur les bords (effet de lentille),
 * liseré lumineux qui accroche la lumiere en haut a gauche et en bas a droite,
 * reflet speculaire. [tint] rend le verre plus laiteux pour lire le texte (0 = clair, 1 = lisible).
 */
class LiquidGlass(private val density: Float) {
    var notch = false
    var tint = 0f

    val path = Path()
    val rect = RectF()

    private fun dp(v: Float) = v * density
    private fun white(a: Int) = Color.argb(a.coerceIn(0, 255), 255, 255, 255)
    private fun lerp(a: Float, b: Float, f: Float) = a + (b - a) * f

    private val body = Paint(Paint.ANTI_ALIAS_FLAG)
    private val outer = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.argb(34, 20, 24, 40)
    }
    private val bevel = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val rim = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val gloss = Paint(Paint.ANTI_ALIAS_FLAG)
    private val caustic = Paint(Paint.ANTI_ALIAS_FLAG)
    private val tmp = RectF()

    /** Prepare la forme pour ce rectangle (coordonnees en pixels). */
    fun build(l: Float, t: Float, r: Float, b: Float) {
        rect.set(l, t, r, b)
        path.reset()
        val w = r - l
        val h = b - t
        if (w <= 0f || h <= 0f) return
        if (!notch) {
            val rad = min(h, w) / 2f
            path.addRoundRect(rect, rad, rad, Path.Direction.CW)
        } else {
            // Encoche : collee au bord du haut, coins du bas arrondis, petites « oreilles » concaves en haut
            val rad = min(min(h * 0.5f, dp(28f)), w / 2f)
            val e = min(dp(10f), h * 0.4f)
            path.moveTo(l - e, t)
            path.quadTo(l, t, l, t + e)
            path.lineTo(l, b - rad)
            tmp.set(l, b - 2 * rad, l + 2 * rad, b)
            path.arcTo(tmp, 180f, -90f)
            path.lineTo(r - rad, b)
            tmp.set(r - 2 * rad, b - 2 * rad, r, b)
            path.arcTo(tmp, 90f, -90f)
            path.lineTo(r, t + e)
            path.quadTo(r, t, r + e, t)
            path.close()
        }

        val f = tint.coerceIn(0f, 1f)
        body.shader = LinearGradient(
            0f, t, 0f, b,
            intArrayOf(
                Color.argb(lerp(30f, 215f, f).toInt(), 252, 253, 255),
                Color.argb(lerp(10f, 190f, f).toInt(), 244, 247, 255)
            ),
            null, Shader.TileMode.CLAMP
        )
        bevel.shader = LinearGradient(
            0f, t, 0f, b,
            intArrayOf(white(255), white(150), white(220)),
            floatArrayOf(0f, 0.55f, 1f), Shader.TileMode.CLAMP
        )
        rim.shader = LinearGradient(
            l, t, r, b,
            intArrayOf(white(255), white(80), white(45), white(215)),
            floatArrayOf(0f, 0.38f, 0.62f, 1f), Shader.TileMode.CLAMP
        )
        val gx = l + min(w * 0.22f, h * 1.1f)
        val gy = t + h * 0.18f
        val gr = max(dp(6f), min(w * 0.35f, h * 0.9f))
        gloss.shader = RadialGradient(gx, gy, gr, white(lerp(120f, 70f, f).toInt()), white(0), Shader.TileMode.CLAMP)
        val cx = r - min(w * 0.18f, h * 0.9f)
        val cy = b - h * 0.12f
        val cr = max(dp(5f), min(w * 0.25f, h * 0.7f))
        caustic.shader = RadialGradient(cx, cy, cr, white(lerp(80f, 40f, f).toInt()), white(0), Shader.TileMode.CLAMP)
    }

    /** Corps du verre (a dessiner avant le contenu). */
    fun drawBody(c: Canvas) {
        if (path.isEmpty) return
        outer.strokeWidth = dp(1.6f)
        c.drawPath(path, outer)
        c.drawPath(path, body)
    }

    /** Epaisseur, reflets et liseré (a dessiner par-dessus le contenu). */
    fun drawEdges(c: Canvas) {
        if (path.isEmpty) return
        c.save()
        c.clipPath(path)
        // Bande interieure : plusieurs traits de plus en plus fins => bord plus lumineux, comme une lentille
        val widths = floatArrayOf(14f, 9f, 5.5f, 3f)
        val alphas = intArrayOf(14, 18, 26, 40)
        for (i in widths.indices) {
            bevel.strokeWidth = dp(widths[i])
            bevel.alpha = alphas[i]
            c.drawPath(path, bevel)
        }
        c.drawRect(rect, gloss)
        c.drawRect(rect, caustic)
        c.restore()
        rim.strokeWidth = dp(1.3f)
        c.drawPath(path, rim)
    }
}
