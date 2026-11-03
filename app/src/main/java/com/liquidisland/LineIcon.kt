package com.liquidisland

import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PixelFormat
import android.graphics.RectF
import android.graphics.drawable.Drawable

/**
 * Icones au trait fin, dans le meme style que celles de Windows 11 (Fluent) utilisees sur PC :
 * contour arrondi de 1,6 dp, sans remplissage. Dessinees sur une grille de 24 x 24.
 */
class LineIcon(private val kind: Kind, private val color: Int, private val density: Float) : Drawable() {

    enum class Kind { FOLDER, IMAGE, VIDEO, MUSIC, DOCUMENT, APPS, PHONE, LAPTOP, CLOSE, SETTINGS, DROP, CHECK, SEND, DOWNLOAD, UPDATE }

    private val p = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
        this.color = this@LineIcon.color
    }
    private val path = Path()
    private val r = RectF()

    override fun getIntrinsicWidth() = (24 * density).toInt()
    override fun getIntrinsicHeight() = (24 * density).toInt()

    override fun draw(c: Canvas) {
        val b = bounds
        val s = minOf(b.width(), b.height()) / 24f
        c.save()
        c.translate(b.left + (b.width() - 24 * s) / 2f, b.top + (b.height() - 24 * s) / 2f)
        c.scale(s, s)
        p.strokeWidth = 1.6f
        path.reset()
        when (kind) {
            Kind.FOLDER -> {
                path.moveTo(3f, 7.5f); path.lineTo(3f, 18f); path.quadTo(3f, 19.5f, 4.5f, 19.5f)
                path.lineTo(19.5f, 19.5f); path.quadTo(21f, 19.5f, 21f, 18f); path.lineTo(21f, 9.5f)
                path.quadTo(21f, 8f, 19.5f, 8f); path.lineTo(12f, 8f); path.lineTo(10f, 5.5f)
                path.lineTo(4.5f, 5.5f); path.quadTo(3f, 5.5f, 3f, 7.5f)
                c.drawPath(path, p)
                c.drawLine(3f, 10.5f, 21f, 10.5f, p)
            }
            Kind.IMAGE -> {
                r.set(3f, 4.5f, 21f, 19.5f); c.drawRoundRect(r, 2.5f, 2.5f, p)
                c.drawCircle(15.5f, 9.5f, 1.6f, p)
                path.moveTo(3.5f, 17f); path.lineTo(9f, 11.5f); path.lineTo(14.5f, 17f)
                path.moveTo(13f, 15.5f); path.lineTo(15.5f, 13f); path.lineTo(20.5f, 18f)
                c.drawPath(path, p)
            }
            Kind.VIDEO -> {
                r.set(2.5f, 6f, 16f, 18f); c.drawRoundRect(r, 2.5f, 2.5f, p)
                path.moveTo(16f, 10.5f); path.lineTo(21f, 7.5f); path.lineTo(21f, 16.5f); path.lineTo(16f, 13.5f)
                c.drawPath(path, p)
            }
            Kind.MUSIC -> {
                path.moveTo(9f, 17.5f); path.lineTo(9f, 5.5f); path.lineTo(19f, 3.5f); path.lineTo(19f, 15.5f)
                c.drawPath(path, p)
                c.drawCircle(6.5f, 17.5f, 2.5f, p)
                c.drawCircle(16.5f, 15.5f, 2.5f, p)
                c.drawLine(9f, 9f, 19f, 7f, p)
            }
            Kind.DOCUMENT -> {
                path.moveTo(6.5f, 3f); path.lineTo(13.5f, 3f); path.lineTo(19f, 8.5f); path.lineTo(19f, 19.5f)
                path.quadTo(19f, 21f, 17.5f, 21f); path.lineTo(6.5f, 21f); path.quadTo(5f, 21f, 5f, 19.5f)
                path.lineTo(5f, 4.5f); path.quadTo(5f, 3f, 6.5f, 3f)
                path.moveTo(13.5f, 3f); path.lineTo(13.5f, 8.5f); path.lineTo(19f, 8.5f)
                c.drawPath(path, p)
                c.drawLine(8.5f, 13f, 15.5f, 13f, p)
                c.drawLine(8.5f, 16.5f, 15.5f, 16.5f, p)
            }
            Kind.APPS -> {
                for ((x, y) in listOf(3.5f to 3.5f, 13.5f to 3.5f, 3.5f to 13.5f, 13.5f to 13.5f)) {
                    r.set(x, y, x + 7f, y + 7f); c.drawRoundRect(r, 2f, 2f, p)
                }
            }
            Kind.PHONE -> {
                r.set(6.5f, 2.5f, 17.5f, 21.5f); c.drawRoundRect(r, 2.8f, 2.8f, p)
                c.drawLine(10.5f, 18.5f, 13.5f, 18.5f, p)
            }
            Kind.LAPTOP -> {
                r.set(4f, 5f, 20f, 15.5f); c.drawRoundRect(r, 1.8f, 1.8f, p)
                c.drawLine(2f, 18.5f, 22f, 18.5f, p)
            }
            Kind.CLOSE -> {
                c.drawLine(6f, 6f, 18f, 18f, p); c.drawLine(18f, 6f, 6f, 18f, p)
            }
            Kind.SETTINGS -> {
                c.drawCircle(12f, 12f, 3f, p)
                for (i in 0 until 8) {
                    val a = Math.toRadians(i * 45.0)
                    val cx = 12f + 7.2f * Math.cos(a).toFloat()
                    val cy = 12f + 7.2f * Math.sin(a).toFloat()
                    c.drawLine(12f + 5.4f * Math.cos(a).toFloat(), 12f + 5.4f * Math.sin(a).toFloat(), cx, cy, p)
                }
                c.drawCircle(12f, 12f, 5.4f, p)
            }
            Kind.DROP -> {
                // Ondes de partage (Wave Drop)
                c.drawCircle(12f, 12f, 2f, p)
                r.set(6f, 6f, 18f, 18f); c.drawArc(r, 200f, 140f, false, p); c.drawArc(r, 20f, 140f, false, p)
                r.set(2.5f, 2.5f, 21.5f, 21.5f); c.drawArc(r, 205f, 130f, false, p); c.drawArc(r, 25f, 130f, false, p)
            }
            Kind.CHECK -> {
                path.moveTo(5f, 12.5f); path.lineTo(10f, 17.5f); path.lineTo(19f, 7f); c.drawPath(path, p)
            }
            Kind.SEND -> {
                path.moveTo(3.5f, 11.5f); path.lineTo(20.5f, 4f); path.lineTo(14f, 20.5f); path.lineTo(11f, 13.5f); path.close()
                c.drawPath(path, p)
                c.drawLine(11f, 13.5f, 20.5f, 4f, p)
            }
            Kind.DOWNLOAD -> {
                c.drawLine(12f, 3.5f, 12f, 15f, p)
                path.moveTo(7.5f, 10.5f); path.lineTo(12f, 15f); path.lineTo(16.5f, 10.5f); c.drawPath(path, p)
                c.drawLine(5f, 20f, 19f, 20f, p)
            }
            Kind.UPDATE -> {
                r.set(4.5f, 4.5f, 19.5f, 19.5f); c.drawArc(r, -60f, 300f, false, p)
                path.moveTo(15.5f, 3.5f); path.lineTo(16f, 6.2f); path.lineTo(13.3f, 6.8f); c.drawPath(path, p)
            }
        }
        c.restore()
    }

    override fun setAlpha(alpha: Int) { p.alpha = alpha }
    override fun setColorFilter(cf: ColorFilter?) { p.colorFilter = cf }
    @Deprecated("Deprecated in Java")
    override fun getOpacity() = PixelFormat.TRANSLUCENT
}
