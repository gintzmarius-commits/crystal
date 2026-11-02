package com.liquidisland

import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.LayerDrawable
import android.graphics.drawable.RippleDrawable

/**
 * Style « liquid glass » sombre de l'appli : fond profond avec des halos de couleur,
 * cartes en verre translucide avec un fin liseré lumineux, pastilles menthe.
 */
object Glass {
    // Textes de l'appli (fond sombre)
    val INK = Color.rgb(240, 246, 243)
    val INK_SOFT = Color.argb(175, 225, 238, 232)
    // Accent menthe, comme la pastille selectionnee de l'image
    val ACCENT = Color.rgb(176, 240, 210)
    val ACCENT_DEEP = Color.rgb(120, 214, 172)
    val ON_ACCENT = Color.rgb(12, 34, 24)
    val OK = Color.rgb(140, 232, 186)
    val WARN = Color.rgb(255, 196, 120)

    // Textes de l'ilot quand il est en verre clair
    val ISLAND_INK = Color.rgb(20, 24, 34)
    val ISLAND_INK_SOFT = Color.rgb(78, 86, 104)

    private fun white(a: Int) = Color.argb(a, 255, 255, 255)

    /** Carte en verre : voile blanc tres leger, plus clair en haut, fin liseré. */
    fun card(density: Float, radius: Float, strong: Boolean = false): Drawable {
        val fill = GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM,
            if (strong) intArrayOf(white(34), white(16)) else intArrayOf(white(22), white(8))
        ).apply {
            cornerRadius = radius
        }
        // Liseré : plus lumineux en haut (on superpose un contour degrade)
        val rimTop = GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(white(0), white(0))
        ).apply {
            cornerRadius = radius
            setStroke(maxOf(1, (1f * density).toInt()), white(if (strong) 70 else 52))
        }
        val shine = GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(white(if (strong) 26 else 18), white(0))
        ).apply {
            cornerRadii = floatArrayOf(radius, radius, radius, radius, 0f, 0f, 0f, 0f)
        }
        return LayerDrawable(arrayOf(fill, shine, rimTop)).apply {
            setLayerGravity(1, android.view.Gravity.TOP or android.view.Gravity.FILL_HORIZONTAL)
            setLayerHeight(1, (36f * density).toInt())
        }
    }

    /** Pastille / bouton principal menthe (texte sombre). */
    fun tinted(density: Float, radius: Float, alpha: Int = 255): Drawable =
        GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(
                Color.argb(alpha, 200, 248, 224),
                Color.argb(alpha, Color.red(ACCENT_DEEP), Color.green(ACCENT_DEEP), Color.blue(ACCENT_DEEP))
            )
        ).apply {
            cornerRadius = radius
            setStroke(maxOf(1, (1f * density).toInt()), white(120))
        }

    /** Petite pastille de statut. */
    fun chip(color: Int, radius: Float): Drawable =
        GradientDrawable().apply {
            cornerRadius = radius
            setColor(Color.argb(34, Color.red(color), Color.green(color), Color.blue(color)))
            setStroke(1, Color.argb(110, Color.red(color), Color.green(color), Color.blue(color)))
        }

    /** Effet d'appui (ripple) clair, adapte au fond sombre. */
    fun pressable(content: Drawable, radius: Float, dark: Boolean = false): Drawable {
        val mask = GradientDrawable().apply { cornerRadius = radius; setColor(Color.WHITE) }
        val ripple = if (dark) Color.argb(40, 12, 34, 24) else white(36)
        return RippleDrawable(ColorStateList.valueOf(ripple), content, mask)
    }

    /** Fond de l'appli : vert-bleu tres sombre avec des halos doux. */
    fun background(widthPx: Int): Drawable {
        val base = GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(Color.rgb(12, 30, 24), Color.rgb(9, 20, 18), Color.rgb(6, 10, 11))
        )
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
                halo(Color.argb(150, 70, 160, 110), 0.15f, 0.12f, w * 0.75f),
                halo(Color.argb(90, 60, 140, 150), 0.9f, 0.3f, w * 0.7f),
                halo(Color.argb(80, 80, 170, 120), 0.3f, 0.62f, w * 0.8f),
                halo(Color.argb(60, 50, 110, 140), 0.85f, 0.9f, w * 0.8f)
            )
        )
    }
}
