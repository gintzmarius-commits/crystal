package com.liquidisland

import android.view.animation.Interpolator
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Ressort physique facon Apple : [damping] < 1 => rebond (0.5 = tres vivant, 0.85 = doux),
 * [response] = duree d'une oscillation en secondes.
 * Utiliser [durationMs] comme duree de l'animation pour que le ressort se pose completement.
 */
class Spring(private val damping: Float, private val response: Float) : Interpolator {
    private val omega = 2.0 * PI / response
    private val zeta = damping.toDouble()
    private val omegaD = omega * sqrt(1.0 - zeta * zeta)
    private val seconds = ln(1000.0) / (zeta * omega)

    val durationMs: Long = (seconds * 1000).toLong()

    override fun getInterpolation(t: Float): Float {
        if (t >= 1f) return 1f
        val tau = t * seconds
        val decay = exp(-zeta * omega * tau)
        return (1.0 - decay * (cos(omegaD * tau) + (zeta * omega / omegaD) * sin(omegaD * tau))).toFloat()
    }

    companion object {
        /** Ouverture : bien rebondissante */
        val OPEN get() = Spring(0.62f, 0.46f)
        /** Ouverture en largeur, un peu plus rapide */
        val OPEN_W get() = Spring(0.6f, 0.4f)
        /** Fermeture : un tout petit rebond */
        val CLOSE get() = Spring(0.8f, 0.38f)
        /** Icone qui « saute » */
        val POP get() = Spring(0.45f, 0.42f)
        /** Contenu */
        val SOFT get() = Spring(0.75f, 0.4f)
    }
}
