package com.liquidisland

import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.LayerDrawable
import android.graphics.drawable.RippleDrawable
import android.view.Gravity

/** Tous les dessins « verre liquide » de l'appli et de l'ilot. */
object Glass {
    val INK = Color.rgb(24, 27, 42)
    val INK_SOFT = Color.rgb(88, 94, 116)
    val ACCENT = Color.rgb(86, 118, 255)
    val ACCENT_2 = Color.rgb(150, 106, 255)
    val OK = Color.rgb(28, 160, 104)
    val WARN = Color.rgb(222, 128, 30)

    private fun white(a: Int) = Color.argb(a, 255, 255, 255)

    /** Couche de reflet en haut d'une forme. */
    private fun shine(radius: Float, alpha: Int): GradientDrawable =
        GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(white(alpha), white(0))).apply {
            cornerRadii = floatArrayOf(radius, radius, radius, radius, 0f, 0f, 0f, 0f)
        }

    /** Carte en verre clair. */
    fun card(density: Float, radius: Float, strong: Boolean = false): Drawable {
        val fill = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            if (strong) intArrayOf(white(215), white(170), white(195))
            else intArrayOf(white(165), white(105), white(140))
        ).apply {
            cornerRadius = radius
            setStroke(maxOf(1, (1f * density).toInt()), white(225))
        }
        val inset = (1.5f * density).toInt()
        return LayerDrawable(arrayOf(fill, shine(radius, 120))).apply {
            setLayerGravity(1, Gravity.TOP or Gravity.FILL_HORIZONTAL)
            setLayerHeight(1, (40f * density).toInt())
            setLayerInsetLeft(1, inset)
            setLayerInsetRight(1, inset)
            setLayerInsetTop(1, inset)
        }
    }

    /** Verre teinté (bleu → violet) pour la carte principale et le bouton principal. */
    fun tinted(density: Float, radius: Float, alpha: Int = 215): Drawable {
        val fill = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            intArrayOf(
                Color.argb(alpha, Color.red(ACCENT), Color.green(ACCENT), Color.blue(ACCENT)),
                Color.argb(alpha, Color.red(ACCENT_2), Color.green(ACCENT_2), Color.blue(ACCENT_2))
            )
        ).apply {
            cornerRadius = radius
            setStroke(maxOf(1, (1f * density).toInt()), white(120))
        }
        val inset = (1.5f * density).toInt()
        return LayerDrawable(arrayOf(fill, shine(radius, 90))).apply {
            setLayerGravity(1, Gravity.TOP or Gravity.FILL_HORIZONTAL)
            setLayerHeight(1, (34f * density).toInt())
            setLayerInsetLeft(1, inset)
            setLayerInsetRight(1, inset)
            setLayerInsetTop(1, inset)
        }
    }

    /** Petite pastille de statut (OK / À faire). */
    fun chip(color: Int, radius: Float): Drawable =
        GradientDrawable().apply {
            cornerRadius = radius
            setColor(Color.argb(38, Color.red(color), Color.green(color), Color.blue(color)))
            setStroke(1, Color.argb(90, Color.red(color), Color.green(color), Color.blue(color)))
        }

    /** Ajoute l'effet d'appui (ripple) sur un fond. */
    fun pressable(content: Drawable, radius: Float, dark: Boolean = true): Drawable {
        val mask = GradientDrawable().apply { cornerRadius = radius; setColor(Color.WHITE) }
        val ripple = if (dark) Color.argb(36, 24, 27, 42) else white(70)
        return RippleDrawable(ColorStateList.valueOf(ripple), content, mask)
    }

    /** Fond de l'appli : dégradé pâle avec des halos de couleur. */
    fun background(widthPx: Int): Drawable {
        val base = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            intArrayOf(Color.rgb(233, 238, 252), Color.rgb(244, 238, 251), Color.rgb(229, 243, 250))
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
                halo(Color.argb(150, 130, 165, 255), 0.1f, 0.08f, w * 0.9f),
                halo(Color.argb(120, 210, 150, 255), 0.95f, 0.38f, w * 0.85f),
                halo(Color.argb(110, 120, 225, 215), 0.2f, 0.9f, w * 0.9f)
            )
        )
    }

    /**
     * Fond de l'ilot. Les coins font toujours la moitié de la hauteur (forme de capsule).
     * Avec le flou actif, le verre est plus transparent ; sans flou, il est plus opaque pour rester lisible.
     */
    fun island(density: Float, w: Int, h: Int, blur: Boolean): Drawable {
        val radius = h / 2f
        val fill = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            if (blur) intArrayOf(white(120), Color.argb(70, 225, 234, 255), white(105))
            else intArrayOf(white(235), Color.argb(215, 232, 238, 255), white(225))
        ).apply {
            cornerRadius = radius
            setStroke(maxOf(1, (1f * density).toInt()), white(210))
        }
        val inset = (2f * density).toInt()
        return LayerDrawable(arrayOf(fill, shine(radius, 110))).apply {
            setLayerGravity(1, Gravity.TOP or Gravity.FILL_HORIZONTAL)
            setLayerHeight(1, h / 2)
            setLayerInsetLeft(1, inset + (radius / 3).toInt())
            setLayerInsetRight(1, inset + (radius / 3).toInt())
            setLayerInsetTop(1, inset)
            setBounds(0, 0, w, h)
        }
    }
}
