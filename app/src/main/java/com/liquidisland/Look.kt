package com.liquidisland

import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.LayerDrawable
import android.graphics.drawable.RippleDrawable

/**
 * Look de l'appli (ecran des reglages), comme sur le PC : fond clair opaque, cartes blanches bien arrondies,
 * accents en degrade bleu > violet > rose, pastilles de couleur par rubrique.
 * (L'ilot, le dock et les panneaux par-dessus les applis gardent le verre liquide : objet [Glass].)
 */
object Look {
    val INK = Color.rgb(28, 29, 42)
    val INK_SOFT = Color.rgb(108, 112, 134)
    /** Violet de l'accent (textes, petites touches de couleur) */
    val ACCENT = Color.rgb(108, 92, 246)
    val ACCENT_DEEP = Color.rgb(98, 84, 230)
    val ON_ACCENT = Color.WHITE
    val OK = Color.rgb(16, 170, 110)
    val WARN = Color.rgb(230, 120, 20)
    val PAGE = Color.rgb(242, 243, 250)
    val SOFT = Color.rgb(240, 241, 249)
    val LINE = Color.rgb(232, 233, 243)
    val TRACK_OFF = Color.rgb(222, 224, 237)

    val BLUE = Color.rgb(79, 124, 255)
    val VIOLET = Color.rgb(139, 92, 246)
    val PINK = Color.rgb(236, 72, 153)

    /** Couleurs des pastilles de rubrique (dans l'ordre d'apparition). */
    val PALETTE = intArrayOf(
        Color.rgb(79, 124, 255), Color.rgb(139, 92, 246), Color.rgb(16, 185, 129), Color.rgb(245, 158, 11),
        Color.rgb(236, 72, 153), Color.rgb(14, 165, 233), Color.rgb(168, 85, 247), Color.rgb(34, 197, 94),
        Color.rgb(249, 115, 22), Color.rgb(244, 63, 94), Color.rgb(99, 102, 241), Color.rgb(20, 184, 166)
    )

    fun gradient(radius: Float): GradientDrawable =
        GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(BLUE, VIOLET, PINK)).apply { cornerRadius = radius }

    /** Carte blanche, fin contour clair (l'ombre douce vient de l'elevation de la vue). */
    @Suppress("UNUSED_PARAMETER")
    fun card(density: Float, radius: Float, strong: Boolean = false): Drawable =
        GradientDrawable().apply {
            cornerRadius = radius
            setColor(if (strong) Color.WHITE else Color.WHITE)
            setStroke(maxOf(1, (1f * density).toInt()), LINE)
        }

    /** Champ / bouton secondaire : gris tres clair. */
    fun soft(radius: Float): Drawable = GradientDrawable().apply { cornerRadius = radius; setColor(SOFT) }

    /** Bouton principal : degrade colore. */
    @Suppress("UNUSED_PARAMETER")
    fun tinted(density: Float, radius: Float, alpha: Int = 255): Drawable = gradient(radius).apply { this.alpha = alpha }

    /** Petite pastille de statut (fond teinte tres clair). */
    fun chip(color: Int, radius: Float): Drawable =
        GradientDrawable().apply {
            cornerRadius = radius
            setColor(Color.argb(28, Color.red(color), Color.green(color), Color.blue(color)))
        }

    /** Effet d'appui (ripple) adapte au fond clair. */
    fun pressable(content: Drawable, radius: Float, dark: Boolean = false): Drawable {
        val mask = GradientDrawable().apply { cornerRadius = radius; setColor(Color.WHITE) }
        val ripple = if (dark) Color.argb(60, 255, 255, 255) else Color.argb(30, 60, 50, 140)
        return RippleDrawable(ColorStateList.valueOf(ripple), content, mask)
    }

    /** Fond de l'appli : blanc casse opaque avec de tres legers halos colores. */
    fun background(widthPx: Int): Drawable {
        val base = GradientDrawable().apply { setColor(PAGE) }
        fun halo(color: Int, cx: Float, cy: Float, r: Float) = GradientDrawable().apply {
            gradientType = GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf(color, color and 0x00FFFFFF)
            setGradientCenter(cx, cy)
            gradientRadius = r
        }
        val w = widthPx.toFloat()
        return LayerDrawable(
            arrayOf(
                base,
                halo(Color.argb(40, 79, 124, 255), 0.1f, 0.05f, w * 0.8f),
                halo(Color.argb(30, 236, 72, 153), 0.95f, 0.25f, w * 0.7f),
                halo(Color.argb(26, 139, 92, 246), 0.2f, 0.7f, w * 0.8f)
            )
        )
    }

    /** Pastille ronde coloree (degrade clair > couleur). */
    fun bubble(color: Int): Drawable {
        fun light(c: Int) = Color.rgb(minOf(255, Color.red(c) + 60), minOf(255, Color.green(c) + 60), minOf(255, Color.blue(c) + 60))
        return GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(light(color), color)).apply { shape = GradientDrawable.OVAL }
    }
}
