package com.liquidisland

import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import kotlin.math.min

/**
 * Verre liquide facon Apple :
 * - le fond est floute par une fenetre a part (voir IslandService), uniquement dans la forme ;
 * - ici on dessine la matiere : une teinte legere, un reflet doux en haut, une lueur en bas,
 *   un fin liseré lumineux et une touche irisee sur le bord du bas.
 * [tint] : 0 = verre clair (ilot replie), 1 = verre plus dense pour lire (ilot ouvert).
 * [dark] : verre fumé (texte blanc) ou verre laiteux (texte sombre).
 * [blurred] : le fond est vraiment floute derriere ; sinon le verre est plus opaque pour rester lisible.
 */
class LiquidGlass(private val density: Float) {
    var notch = false
    var tint = 0f
    var dark = true
    var blurred = false

    val path = Path()
    val rect = RectF()

    private fun dp(v: Float) = v * density
    private fun lerp(a: Float, b: Float, f: Float) = a + (b - a) * f
    private fun white(a: Float) = Color.argb(a.toInt().coerceIn(0, 255), 255, 255, 255)

    private val body = Paint(Paint.ANTI_ALIAS_FLAG)
    private val sheen = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glow = Paint(Paint.ANTI_ALIAS_FLAG)
    private val edge = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val iris = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val rim = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val shade = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val tmp = RectF()
    private var radius = 0f

    fun build(l: Float, t: Float, r: Float, b: Float) {
        rect.set(l, t, r, b)
        path.reset()
        val w = r - l
        val h = b - t
        if (w <= 0f || h <= 0f) return
        if (!notch) {
            radius = min(h, w) / 2f
            path.addRoundRect(rect, radius, radius, Path.Direction.CW)
        } else {
            // Encoche : collee au bord du haut, coins du bas arrondis, petites « oreilles » concaves en haut
            radius = min(min(h * 0.5f, dp(28f)), w / 2f)
            val e = min(dp(10f), h * 0.4f)
            path.moveTo(l - e, t)
            path.quadTo(l, t, l, t + e)
            path.lineTo(l, b - radius)
            tmp.set(l, b - 2 * radius, l + 2 * radius, b)
            path.arcTo(tmp, 180f, -90f)
            path.lineTo(r - radius, b)
            tmp.set(r - 2 * radius, b - 2 * radius, r, b)
            path.arcTo(tmp, 90f, -90f)
            path.lineTo(r, t + e)
            path.quadTo(r, t, r + e, t)
            path.close()
        }

        val f = tint.coerceIn(0f, 1f)
        // Teinte : plus legere quand le fond est floute derriere
        val aTop: Float
        val aBot: Float
        if (blurred) {
            aTop = lerp(if (dark) 95f else 70f, if (dark) 150f else 150f, f)
            aBot = lerp(if (dark) 120f else 50f, if (dark) 175f else 125f, f)
        } else {
            aTop = lerp(if (dark) 190f else 175f, if (dark) 228f else 225f, f)
            aBot = lerp(if (dark) 210f else 155f, if (dark) 238f else 210f, f)
        }
        body.shader = if (dark) LinearGradient(
            0f, t, 0f, b,
            Color.argb(aTop.toInt(), 34, 38, 46), Color.argb(aBot.toInt(), 14, 16, 22), Shader.TileMode.CLAMP
        ) else LinearGradient(
            0f, t, 0f, b,
            Color.argb(aTop.toInt(), 255, 255, 255), Color.argb(aBot.toInt(), 236, 240, 248), Shader.TileMode.CLAMP
        )

        // Reflet doux qui descend du haut (comme la lumiere sur une goutte)
        sheen.shader = LinearGradient(
            0f, t, 0f, t + h * 0.55f,
            white(if (dark) 34f else 80f), white(0f), Shader.TileMode.CLAMP
        )
        // Lueur qui remonte du bas (la lumiere traverse le verre et ressort en bas)
        glow.shader = LinearGradient(
            0f, b, 0f, b - h * 0.4f,
            white(if (dark) 22f else 45f), white(0f), Shader.TileMode.CLAMP
        )
        // Epaisseur : bord interieur plus lumineux en haut qu'en bas
        edge.shader = LinearGradient(
            0f, t, 0f, b,
            white(if (dark) 60f else 120f), white(if (dark) 14f else 40f), Shader.TileMode.CLAMP
        )
        // Touche irisee sur le bas, comme sur l'image Apple
        iris.shader = LinearGradient(
            l + w * 0.18f, 0f, r - w * 0.18f, 0f,
            intArrayOf(
                Color.argb(0, 120, 220, 255),
                Color.argb(110, 120, 220, 255),
                Color.argb(110, 255, 140, 220),
                Color.argb(100, 255, 225, 130),
                Color.argb(0, 255, 225, 130)
            ),
            floatArrayOf(0f, 0.3f, 0.55f, 0.78f, 1f), Shader.TileMode.CLAMP
        )
        // Liseré : accroche la lumiere en haut a gauche et en bas a droite
        rim.shader = LinearGradient(
            l, t, r, b,
            intArrayOf(
                white(if (dark) 150f else 255f),
                white(if (dark) 45f else 110f),
                white(if (dark) 30f else 80f),
                white(if (dark) 115f else 210f)
            ),
            floatArrayOf(0f, 0.4f, 0.6f, 1f), Shader.TileMode.CLAMP
        )
        shade.color = if (dark) Color.argb(70, 0, 0, 0) else Color.argb(40, 30, 40, 70)
    }

    /** Corps du verre (avant le contenu). */
    fun drawBody(c: Canvas) {
        if (path.isEmpty) return
        // Fine ombre exterieure pour detacher le verre d'un fond clair
        shade.strokeWidth = dp(2.2f)
        c.drawPath(path, shade)
        c.drawPath(path, body)
        c.save()
        c.clipPath(path)
        c.drawRect(rect, sheen)
        c.drawRect(rect, glow)
        c.restore()
    }

    /** Bords et reflets (par-dessus le contenu). */
    fun drawEdges(c: Canvas) {
        if (path.isEmpty) return
        c.save()
        c.clipPath(path)
        // Bord interieur : degrade lisse sur ~5 dp, en fines couches tres legeres (pas d'anneaux visibles)
        val steps = 8
        for (i in 0 until steps) {
            val k = (i + 1).toFloat() / steps
            edge.strokeWidth = dp(10f * (1f - k) + 1.2f)
            edge.alpha = (14 * k + 4).toInt()
            c.drawPath(path, edge)
        }
        // Irisation : seulement sur le bas du bord
        if (rect.height() > dp(20f)) {
            c.clipRect(rect.left, rect.bottom - rect.height() * 0.35f, rect.right, rect.bottom)
            iris.strokeWidth = dp(2.4f)
            c.drawPath(path, iris)
        }
        c.restore()
        rim.strokeWidth = dp(1f)
        c.drawPath(path, rim)
    }
}
