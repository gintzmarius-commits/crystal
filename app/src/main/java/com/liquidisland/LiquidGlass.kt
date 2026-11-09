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
import kotlin.math.abs
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
    /** Rayon des coins impose (panneaux) ; 0 = capsule (moitie de la hauteur) */
    var fixedRadius = 0f
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
    /** Sans capture de l'ecran : lueur sous le bord oppose a la lumiere (la lumiere qui traverse la goutte) */
    private val caustic = Paint(Paint.ANTI_ALIAS_FLAG)
    private val causticMatrix = Matrix()
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
    private val k = FloatArray(10)
    // Degrades calcules une seule fois en coordonnees 0..1, puis simplement places sur la forme a chaque image
    // (avant : 5 degrades recrees a chaque image de l'animation -> ramasse-miettes et saccades)
    private val shaderKey = FloatArray(6) { Float.NaN }
    private val unitMatrix = Matrix()
    private val bodyMatrix = Matrix()
    // La lumiere ne recree plus les degrades : elle les deplace/tourne avec ces matrices (zero allocation)
    private val edgeMatrix = Matrix()
    private val irisMatrix = Matrix()
    private val rimMatrix = Matrix()
    private val polySrc = floatArrayOf(0f, 0f, 0f, 1f)
    private val polyDst = FloatArray(4)
    /** Teinte du corps : un degrade par palier de teinte (49 paliers), cree une fois puis reutilise pendant les animations */
    private val bodyCache = arrayOfNulls<Shader>(TINT_STEPS + 1)
    private var themeKey = Float.NaN

    /** Vrai si la forme ou la lumiere ont change depuis le dernier calcul (sinon on garde tout en cache). */
    private fun changed(l: Float, t: Float, r: Float, b: Float): Boolean {
        k[0] = l; k[1] = t; k[2] = r; k[3] = b; k[4] = tint; k[5] = lightX; k[6] = lightY
        k[7] = (if (dark) 1f else 0f) + (if (clear) 2f else 0f); k[8] = if (blurred) 1f else 0f; k[9] = if (notch) 1f else 0f
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
            radius = if (fixedRadius > 0f) min(fixedRadius, min(h, w) / 2f) else min(h, w) / 2f
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

        val lx = lightX.coerceIn(-1f, 1f)
        val ly = lightY.coerceIn(-1f, 1f)
        val cx = (l + r) / 2f
        rebuildShaders(lx, ly)

        // Placement des degrades sur la forme (aucune allocation)
        unitMatrix.setScale(w, h)
        unitMatrix.postTranslate(l, t)
        bodyMatrix.setScale(1f, h)
        bodyMatrix.postTranslate(0f, t)
        body.shader.setLocalMatrix(bodyMatrix)
        // Epaisseur : degrade canonique (0,0)->(0,1) amene sur (0.5-lx/2, -ly)->(0.5+lx/2, 1.2) puis sur la forme
        polyDst[0] = 0.5f - lx * 0.5f; polyDst[1] = -ly; polyDst[2] = 0.5f + lx * 0.5f; polyDst[3] = 1.2f
        edgeMatrix.setPolyToPoly(polySrc, 0, polyDst, 0, 2)
        edgeMatrix.postConcat(unitMatrix)
        edge.shader.setLocalMatrix(edgeMatrix)
        // Touche irisee et liseré : simplement decales avec la lumiere
        irisMatrix.setTranslate(-lx * 0.1f, 0f)
        irisMatrix.postConcat(unitMatrix)
        iris.shader.setLocalMatrix(irisMatrix)
        rimMatrix.setTranslate(lx * 0.3f, 0f)
        rimMatrix.postConcat(unitMatrix)
        rim.shader.setLocalMatrix(rimMatrix)
        innerRim.shader.setLocalMatrix(bodyMatrix)
        // Reflet speculaire : tache allongee pres du bord du haut, qui glisse avec l'inclinaison
        val sx = cx + lx * w * 0.32f
        val sy = t + h * (0.16f + 0.06f * ly)
        val sr = max(dp(8f), h * 0.55f)
        specMatrix.setScale(sr * min(3.2f, max(1f, w / h * 0.9f)), sr * 0.55f)
        specMatrix.postTranslate(sx, sy)
        spec.shader.setLocalMatrix(specMatrix)
        val cxr = cx - lx * w * 0.28f
        val cyr = b - h * (0.14f - 0.04f * ly)
        causticMatrix.setScale(sr * min(2.6f, max(1f, w / h * 0.7f)), sr * 0.42f)
        causticMatrix.postTranslate(cxr, cyr)
        caustic.shader?.setLocalMatrix(causticMatrix)

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

    /**
     * Degrades : ceux qui ne dependent que du theme sont crees une fois par theme ; la lumiere ne fait que les
     * deplacer (matrices, dans [build]) ; la teinte choisit un degrade deja pret (un par palier).
     * Avant : jusqu'a 7 degrades recrees a chaque image d'animation et a chaque mouvement du telephone.
     */
    @Suppress("UNUSED_PARAMETER")
    private fun rebuildShaders(lx: Float, ly: Float) {
        val theme = (if (dark) 1f else 0f) + (if (clear) 2f else 0f) + (if (blurred) 4f else 0f)
        if (theme != themeKey || edge.shader == null) {
            themeKey = theme
            java.util.Arrays.fill(bodyCache, null)
            spec.shader = RadialGradient(0f, 0f, 1f, white(85f), white(0f), Shader.TileMode.CLAMP)
            caustic.shader = RadialGradient(0f, 0f, 1f, white(if (dark) 42f else 60f), white(0f), Shader.TileMode.CLAMP)
            // Epaisseur : bord interieur clair cote lumiere, plus sombre a l'oppose (orientation donnee par la matrice)
            edge.shader = LinearGradient(0f, 0f, 0f, 1f,
                white(if (dark) 70f else 110f), black(if (dark) 55f else 28f), Shader.TileMode.CLAMP)
            // Touche irisee sur le bas, comme sur l'image Apple
            iris.shader = LinearGradient(0.15f, 0f, 0.85f, 0f,
                intArrayOf(
                    Color.argb(0, 120, 220, 255), Color.argb(120, 120, 220, 255), Color.argb(120, 255, 140, 220),
                    Color.argb(110, 255, 225, 130), Color.argb(0, 255, 225, 130)
                ),
                floatArrayOf(0f, 0.3f, 0.55f, 0.78f, 1f), Shader.TileMode.CLAMP)
            // Liseré : accroche la lumiere d'un cote et le bas oppose
            rim.shader = LinearGradient(0f, 0f, 1f, 1f,
                intArrayOf(white(235f), white(70f), white(40f), white(170f)),
                floatArrayOf(0f, 0.42f, 0.6f, 1f), Shader.TileMode.CLAMP)
            // Second liseré interieur tres fin (le « double contour » du verre epais)
            innerRim.shader = LinearGradient(0f, 0f, 0f, 1f, white(70f), white(25f), Shader.TileMode.CLAMP)
        }
        val step = (tint.coerceIn(0f, 1f) * TINT_STEPS + 0.5f).toInt()
        body.shader = bodyCache[step] ?: bodyShader(step.toFloat() / TINT_STEPS).also { bodyCache[step] = it }
    }

    /** Corps teinte : quasi nul quand l'ilot est replie (verre transparent), plus dense ouvert pour lire. */
    private fun bodyShader(f: Float): Shader {
        val aTop: Float
        val aBot: Float
        if (clear) {
            aTop = lerp(8f, 26f, f)
            aBot = lerp(14f, 34f, f)
        } else if (blurred) {
            aTop = lerp(if (dark) 60f else 50f, 145f, f)
            aBot = lerp(if (dark) 80f else 40f, if (dark) 170f else 125f, f)
        } else {
            aTop = lerp(if (dark) 14f else 10f, if (dark) 168f else 175f, f)
            aBot = lerp(if (dark) 22f else 6f, if (dark) 190f else 150f, f)
        }
        return if (dark) LinearGradient(0f, 0f, 0f, 1f,
            Color.argb(aTop.toInt(), 30, 34, 42), Color.argb(aBot.toInt(), 10, 12, 18), Shader.TileMode.CLAMP)
        else LinearGradient(0f, 0f, 0f, 1f,
            Color.argb(aTop.toInt(), 255, 255, 255), Color.argb(aBot.toInt(), 238, 242, 250), Shader.TileMode.CLAMP)
    }

    /** Corps du verre (avant le contenu) : fond deforme sur les bords, puis teinte. */
    fun drawBody(c: Canvas) {
        if (path.isEmpty) return
        shade.strokeWidth = dp(2f)
        c.drawPath(path, shade)
        // Dessin sans carte graphique (certains Xiaomi) : pas de shader, sinon Android plante
        if (Build.VERSION.SDK_INT >= 33 && c.isHardwareAccelerated && backdrop != null && refract != null && fade > 0.01f) {
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
        // Sans deformation du fond (pas de capture) : bord plus epais et plus lumineux, comme une goutte d'eau
        val liquid = backdrop == null || fade < 0.01f
        val boost = if (liquid) 2.3f else 1f
        val thick = if (liquid) 16f else 12f
        val steps = 5
        for (i in 0 until steps) {
            val k = (i + 1).toFloat() / steps
            edge.strokeWidth = dp(thick * (1f - k) + 1.2f)
            edge.alpha = ((16 * k + 4) * boost).toInt().coerceAtMost(255)
            c.drawPath(path, edge)
        }
        c.drawRect(rect, spec)
        if (liquid && rect.height() > dp(14f)) c.drawRect(rect, caustic)
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
        /** Paliers de teinte gardes en cache (1/48 : invisible a l'oeil pendant une animation) */
        const val TINT_STEPS = 48

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
