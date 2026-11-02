package com.liquidisland

import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.RuntimeShader
import android.graphics.Shader
import android.os.Build
import kotlin.math.max
import kotlin.math.min

/**
 * Verre liquide facon Apple, transparent :
 * - le centre laisse voir le fond tel quel (en direct) ;
 * - sur le bord, le fond est deforme comme a travers une goutte d'eau (refraction + legere
 *   separation des couleurs), a partir d'une capture de l'ecran (Android 13+) ;
 * - liseré fin, epaisseur douce et reflet qui suivent l'inclinaison du telephone ([lightX], [lightY]).
 * [tint] : 0 = verre clair (ilot replie), 1 = verre plus dense pour lire (ilot ouvert).
 */
class LiquidGlass(private val density: Float) {
    var notch = false
    var tint = 0f
    var dark = true
    var blurred = false
    /** Verre totalement transparent, meme ouvert (pas de teinte du tout) */
    var clear = true
    /** Direction de la lumiere, -1..1 (inclinaison du telephone). */
    var lightX = 0f
    var lightY = 0f
    /** 0 = bord deforme cache (le fond vient de changer), 1 = visible */
    var fade = 1f

    val path = Path()
    val rect = RectF()

    private fun dp(v: Float) = v * density
    private fun lerp(a: Float, b: Float, f: Float) = a + (b - a) * f
    private fun white(a: Float) = Color.argb(a.toInt().coerceIn(0, 255), 255, 255, 255)
    private fun black(a: Float) = Color.argb(a.toInt().coerceIn(0, 255), 0, 0, 0)

    private val body = Paint(Paint.ANTI_ALIAS_FLAG)
    private val spec = Paint(Paint.ANTI_ALIAS_FLAG)
    private val edge = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val iris = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val rim = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val innerRim = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val shade = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val refractPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private var lensPx = 0f
    private val innerPath = Path()
    private val tmp = RectF()
    private val specMatrix = Matrix()
    private var radius = 0f

    // ---------- Refraction (Android 13+) ----------

    private var refract: RuntimeShader? = null
    /** Image de l'ecran derriere l'ilot (deja placee dans les coordonnees de la vue). */
    var backdrop: Shader? = null
        set(value) {
            field = value
            if (Build.VERSION.SDK_INT >= 33 && value != null) {
                val r = refract ?: RuntimeShader(REFRACT_SHADER).also { refract = it }
                r.setInputShader("bg", value)
                refractPaint.shader = r
                invalidateCache()
            }
        }

    private val key = FloatArray(10) { Float.NaN }

    /** Vrai si la forme ou la lumiere ont change depuis le dernier calcul (sinon on garde tout en cache). */
    private fun changed(l: Float, t: Float, r: Float, b: Float): Boolean {
        val k = floatArrayOf(l, t, r, b, tint, lightX, lightY, (if (dark) 1f else 0f) + (if (clear) 2f else 0f), if (blurred) 1f else 0f, if (notch) 1f else 0f)
        var diff = false
        for (i in k.indices) if (kotlin.math.abs(k[i] - key[i]) > 0.0005f || key[i].isNaN()) { diff = true; break }
        if (diff) k.copyInto(key)
        return diff
    }

    /** Force un nouveau calcul (ex. nouvelle image du fond). */
    fun invalidateCache() { key[0] = Float.NaN }

    fun build(l: Float, t: Float, r: Float, b: Float) {
        if (!changed(l, t, r, b)) return
        rect.set(l, t, r, b)
        path.reset()
        innerPath.reset()
        val w = r - l
        val h = b - t
        if (w <= 0f || h <= 0f) return
        val inset = dp(1.8f)
        if (!notch) {
            radius = min(h, w) / 2f
            path.addRoundRect(rect, radius, radius, Path.Direction.CW)
            tmp.set(l + inset, t + inset, r - inset, b - inset)
            val ri = max(0f, radius - inset)
            innerPath.addRoundRect(tmp, ri, ri, Path.Direction.CW)
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
        // Teinte : quasi nulle quand l'ilot est replie (verre transparent), plus dense ouvert pour lire
        val aTop: Float
        val aBot: Float
        if (clear) {
            // Transparent : juste un voile infime qui donne de la matiere, sans blanchir le fond
            aTop = lerp(8f, 26f, f)
            aBot = lerp(14f, 34f, f)
        } else if (blurred) {
            aTop = lerp(if (dark) 60f else 50f, 145f, f)
            aBot = lerp(if (dark) 80f else 40f, if (dark) 170f else 125f, f)
        } else {
            aTop = lerp(if (dark) 14f else 10f, if (dark) 168f else 175f, f)
            aBot = lerp(if (dark) 22f else 6f, if (dark) 190f else 150f, f)
        }
        body.shader = if (dark) LinearGradient(
            0f, t, 0f, b,
            Color.argb(aTop.toInt(), 30, 34, 42), Color.argb(aBot.toInt(), 10, 12, 18), Shader.TileMode.CLAMP
        ) else LinearGradient(
            0f, t, 0f, b,
            Color.argb(aTop.toInt(), 255, 255, 255), Color.argb(aBot.toInt(), 238, 242, 250), Shader.TileMode.CLAMP
        )

        // La lumiere vient du haut, decalee selon l'inclinaison
        val lx = lightX.coerceIn(-1f, 1f)
        val ly = lightY.coerceIn(-1f, 1f)
        val cx = (l + r) / 2f

        // Reflet speculaire : tache allongee pres du bord du haut, qui glisse avec l'inclinaison
        val sx = cx + lx * w * 0.32f
        val sy = t + h * (0.16f + 0.06f * ly)
        val sr = max(dp(8f), h * 0.55f)
        val g = RadialGradient(sx, sy, sr, white(85f), white(0f), Shader.TileMode.CLAMP)
        specMatrix.reset()
        specMatrix.setScale(min(3.2f, max(1f, w / h * 0.9f)), 0.55f, sx, sy)
        g.setLocalMatrix(specMatrix)
        spec.shader = g

        // Epaisseur : bord interieur clair cote lumiere, plus sombre a l'oppose
        edge.shader = LinearGradient(
            cx - lx * w * 0.5f, t - ly * h, cx + lx * w * 0.5f, b + h * 0.2f,
            white(if (dark) 70f else 110f), black(if (dark) 55f else 28f), Shader.TileMode.CLAMP
        )
        // Touche irisee sur le bas, comme sur l'image Apple
        iris.shader = LinearGradient(
            l + w * (0.15f - lx * 0.1f), 0f, r - w * (0.15f + lx * 0.1f), 0f,
            intArrayOf(
                Color.argb(0, 120, 220, 255),
                Color.argb(120, 120, 220, 255),
                Color.argb(120, 255, 140, 220),
                Color.argb(110, 255, 225, 130),
                Color.argb(0, 255, 225, 130)
            ),
            floatArrayOf(0f, 0.3f, 0.55f, 0.78f, 1f), Shader.TileMode.CLAMP
        )
        // Liseré : accroche la lumiere d'un cote et le bas oppose
        rim.shader = LinearGradient(
            cx - w * 0.5f + lx * w * 0.3f, t, cx + w * 0.5f + lx * w * 0.3f, b,
            intArrayOf(white(235f), white(70f), white(40f), white(170f)),
            floatArrayOf(0f, 0.42f, 0.6f, 1f), Shader.TileMode.CLAMP
        )
        // Second liseré interieur tres fin (le « double contour » du verre epais)
        innerRim.shader = LinearGradient(
            0f, t, 0f, b, white(70f), white(25f), Shader.TileMode.CLAMP
        )
        shade.color = if (dark) Color.argb(60, 0, 0, 0) else Color.argb(36, 30, 40, 70)

        // Parametres de la refraction
        refract?.let { rs ->
            if (Build.VERSION.SDK_INT >= 33) {
                // Pour l'encoche, la forme continue au-dessus de l'ecran : pas de bord en haut
                val top = if (notch) t - radius * 2f else t
                rs.setFloatUniform("center", cx, (top + b) / 2f)
                rs.setFloatUniform("halfSize", w / 2f, (b - top) / 2f)
                rs.setFloatUniform("radius", radius)
                // Goutte d'eau : la lentille couvre presque toute la capsule repliee, un large bord une fois ouverte
                // Large sur la petite capsule (goutte), fine sur une grande surface ouverte
                val tt = tint.coerceIn(0f, 1f)
                lensPx = lerp(min(dp(24f), min(w, h) * 0.5f), min(dp(9f), min(w, h) * 0.5f), tt)
                rs.setFloatUniform("lens", lensPx)
                rs.setFloatUniform("strength", lerp(min(dp(20f), h * 0.6f), dp(7f), tt))
                rs.setFloatUniform("disp", 0.07f)
                rs.setFloatUniform("minOff", dp(3f))
                rs.setFloatUniform("toLight", lx * 0.9f, -1f + ly * 0.4f)
                rs.setFloatUniform("clearTint", if (clear) 1f else 1f - tint.coerceIn(0f, 1f))
            }
        }
    }

    /** Corps du verre (avant le contenu) : fond deforme sur les bords, puis teinte. */
    fun drawBody(c: Canvas) {
        if (path.isEmpty) return
        shade.strokeWidth = dp(2f)
        c.drawPath(path, shade)
        if (Build.VERSION.SDK_INT >= 33 && backdrop != null && refract != null && fade > 0.01f) {
            // On ne calcule la lentille que sur la bande du bord (le centre reste transparent)
            refract?.setFloatUniform("fade", fade.coerceIn(0f, 1f))
            c.save()
            c.clipPath(path)
            refractPaint.strokeWidth = lensPx * 2f + 2f
            c.drawPath(path, refractPaint)
            c.restore()
        }
        c.drawPath(path, body)
    }

    /** Bords et reflets (par-dessus le contenu). */
    fun drawEdges(c: Canvas) {
        if (path.isEmpty) return
        c.save()
        c.clipPath(path)
        // Epaisseur : degrade lisse sur ~6 dp, en couches tres legeres
        val steps = 5
        for (i in 0 until steps) {
            val k = (i + 1).toFloat() / steps
            edge.strokeWidth = dp(12f * (1f - k) + 1.2f)
            edge.alpha = (16 * k + 4).toInt()
            c.drawPath(path, edge)
        }
        c.drawRect(rect, spec)
        if (!innerPath.isEmpty) {
            innerRim.strokeWidth = dp(0.8f)
            c.drawPath(innerPath, innerRim)
        }
        if (rect.height() > dp(20f)) {
            c.clipRect(rect.left, rect.bottom - rect.height() * 0.35f, rect.right, rect.bottom)
            iris.strokeWidth = dp(2.4f)
            c.drawPath(path, iris)
        }
        c.restore()
        rim.strokeWidth = dp(1.1f)
        c.drawPath(path, rim)
    }

    companion object {
        /**
         * Shader AGSL : a l'interieur de la forme, pres du bord, on affiche le fond pris un peu plus
         * a l'exterieur (comme une lentille) avec les 3 couleurs legerement decalees.
         * Au centre, rien n'est dessine : le vrai fond reste visible en direct.
         */
        const val REFRACT_SHADER = """
            uniform shader bg;
            uniform float2 center;
            uniform float2 halfSize;
            uniform float radius;
            uniform float lens;
            uniform float strength;
            uniform float disp;
            uniform float minOff;
            uniform float2 toLight;
            uniform float fade;
            uniform float clearTint;

            float sdBox(float2 p) {
                float2 q = abs(p - center) - halfSize + radius;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
            }

            half4 main(float2 p) {
                float d = sdBox(p);
                if (d > 0.0) { return half4(0.0); }
                float inside = -d;
                if (inside > lens) { return half4(0.0); }
                float2 g = float2(sdBox(p + float2(1.0, 0.0)) - sdBox(p - float2(1.0, 0.0)),
                                  sdBox(p + float2(0.0, 1.0)) - sdBox(p - float2(0.0, 1.0)));
                float2 n = normalize(g + float2(0.00001, 0.00001));
                float t = 1.0 - inside / lens;
                float k = t * t;
                // On prend toujours le fond a l'EXTERIEUR de la forme : l'ilot ne se « voit » pas lui-meme
                float2 off = n * (inside + strength * k + minOff);
                half4 cr = bg.eval(p + off * (1.0 + disp));
                half4 cg = bg.eval(p + off);
                half4 cb = bg.eval(p + off * (1.0 - disp));
                half3 col = half3(cr.r, cg.g, cb.b);
                // Comme l'eau : le bord s'assombrit un peu (la lumiere y est renvoyee)
                col *= half(mix(1.0, 0.78, k));
                // Reflets : la lumiere accroche le bord tourne vers elle, et ressort de l'autre cote
                float2 L = normalize(toLight);
                float shine = pow(max(dot(n, L), 0.0), 4.0) * t * t;
                float caustic = pow(max(dot(n, -L), 0.0), 3.0) * t * t;
                col += half3(shine * 0.75 + caustic * 0.28);
                // Fondu vers le centre : plus le verre est clair, plus la lentille est visible
                float a = smoothstep(0.0, 0.55, t) * mix(0.75, 1.0, clearTint) * fade;
                return half4(col, 1.0) * half(a);
            }
        """
    }
}
