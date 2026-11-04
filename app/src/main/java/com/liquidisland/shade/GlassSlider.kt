package com.liquidisland.shade

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import com.liquidisland.LineIcon
import com.liquidisland.LiquidGlass

/**
 * Grand curseur vertical en verre liquide (luminosite, volume), comme le centre de controle de l'iPhone :
 * on glisse n'importe ou dessus, la partie remplie suit le doigt.
 */
@SuppressLint("ViewConstructor")
class GlassSlider(ctx: Context, kind: LineIcon.Kind, initial: Float, private val onChange: (Float) -> Unit) : View(ctx) {
    private val d = ctx.resources.displayMetrics.density
    private val glass = LiquidGlass(d).apply {
        clear = true
        dark = true
        tint = 0f
        fixedRadius = 26 * d
        lightX = -0.3f
        lightY = -0.6f
    }
    private val bg = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(80, 18, 22, 30) }
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(238, 255, 255, 255) }
    private val iconDark = LineIcon(kind, Color.rgb(28, 32, 40), d)
    private val iconLight = LineIcon(kind, Color.WHITE, d)
    private val r = RectF()
    var value = initial.coerceIn(0f, 1f)
        set(v) { field = v.coerceIn(0f, 1f); invalidate() }

    var dragging = false
        private set
    private var startY = 0f
    private var startV = 0f
    private var lastStep = -1

    override fun onDraw(c: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        glass.build(0f, 0f, w, h)
        glass.drawBody(c)
        r.set(0f, 0f, w, h)
        c.drawRoundRect(r, glass.fixedRadius, glass.fixedRadius, bg)
        c.save()
        c.clipPath(glass.path)
        c.drawRect(0f, h * (1f - value), w, h, fill)
        c.restore()
        glass.drawEdges(c)
        val s = (26 * d).toInt()
        val ic = if (value > (34 * d) / h) iconDark else iconLight
        ic.setBounds(((w - s) / 2).toInt(), (h - s - 16 * d).toInt(), ((w + s) / 2).toInt(), (h - 16 * d).toInt())
        ic.draw(c)
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                parent?.requestDisallowInterceptTouchEvent(true)
                dragging = true
                startY = e.y
                startV = value
                animate().scaleX(1.04f).scaleY(1.02f).setDuration(120).start()
            }
            MotionEvent.ACTION_MOVE -> {
                value = startV - (e.y - startY) / height.coerceAtLeast(1)
                val step = (value * 16).toInt()
                if (step != lastStep) { lastStep = step; if (value <= 0f || value >= 1f) performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK) }
                onChange(value)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                dragging = false
                animate().scaleX(1f).scaleY(1f).setDuration(160).start()
                onChange(value)
            }
        }
        return true
    }
}
