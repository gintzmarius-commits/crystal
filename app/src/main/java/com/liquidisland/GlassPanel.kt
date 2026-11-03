package com.liquidisland

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.widget.LinearLayout

/**
 * Panneau en verre liquide transparent (comme le menu de la touche Windows sur PC) :
 * on voit l'ecran a travers, avec le liseré lumineux, l'epaisseur et les reflets du verre de l'ilot.
 * Les textes et les icones sont poses sur de petits carres pour rester lisibles sur n'importe quel fond.
 */
class GlassPanel(ctx: Context, radiusDp: Float) : LinearLayout(ctx) {
    private val d = ctx.resources.displayMetrics.density
    val glass = LiquidGlass(d).apply {
        clear = true
        dark = true
        tint = 0f
        fixedRadius = radiusDp * d
        lightX = -0.3f
        lightY = -0.6f
    }

    init {
        orientation = VERTICAL
        setWillNotDraw(false)
    }

    override fun dispatchDraw(canvas: Canvas) {
        glass.build(0f, 0f, width.toFloat(), height.toFloat())
        glass.drawBody(canvas)
        super.dispatchDraw(canvas)
        glass.drawEdges(canvas)
    }

    companion object {
        /** Petit carre sombre et translucide sous un texte ou une icone. */
        fun plate(c: Context, radiusDp: Float = 16f, strong: Boolean = false): GradientDrawable {
            val dd = c.resources.displayMetrics.density
            return GradientDrawable().apply {
                cornerRadius = radiusDp * dd
                setColor(Color.argb(if (strong) 160 else 115, 16, 20, 22))
                setStroke(maxOf(1, dd.toInt()), Color.argb(if (strong) 70 else 45, 255, 255, 255))
            }
        }
    }
}
