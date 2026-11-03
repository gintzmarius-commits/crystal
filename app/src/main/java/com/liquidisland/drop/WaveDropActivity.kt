package com.liquidisland.drop

import android.animation.ValueAnimator
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.nfc.NfcAdapter
import android.nfc.tech.IsoDep
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.text.TextUtils
import android.util.Log
import android.util.Size
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import com.liquidisland.Glass
import com.liquidisland.GlassPanel
import com.liquidisland.LineIcon
import com.liquidisland.Prefs
import com.liquidisland.Spring
import kotlin.math.max

/**
 * Le panneau Wave Drop : il coule depuis l'ilot (glisser l'ilot vers le bas).
 * 1. On choisit quoi envoyer (fichiers, images, videos, musique, documents, applis).
 * 2. On colle l'autre appareil (NFC) ou on le rapproche (Bluetooth) : il apparait, on touche « Envoyer ».
 * En mode « demande », il affiche le fichier propose avec Accepter / Refuser.
 */
class WaveDropActivity : Activity(), WaveDrop.Ui {

    private val d get() = resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private fun dpf(v: Int) = v * d

    private lateinit var panel: GlassPanel
    private lateinit var body: LinearLayout
    private lateinit var status: TextView
    private lateinit var bar: ProgressBar
    private var nearbyBox: LinearLayout? = null
    private var request: WaveDrop.Request? = null
    private var closing = false
    private var nfc: NfcAdapter? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WaveDrop.init(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS)
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT
        // Le contenu commence tout en haut de l'ecran : le panneau se place juste sous l'ilot
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        nfc = try { NfcAdapter.getDefaultAdapter(this) } catch (e: Exception) { null }

        val root = FrameLayout(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            // Toucher a cote du panneau le referme
            setOnTouchListener { _, e -> if (e.action == MotionEvent.ACTION_UP) close(); true }
        }
        // Panneau en verre liquide transparent, comme sur PC (pas de fond sombre)
        panel = GlassPanel(this, 34f).apply {
            setPadding(dp(18), dp(18), dp(18), dp(20))
            isClickable = true
        }
        val top = dp(if (Prefs.notch(this)) 0 else Prefs.offset(this)) + dp(Prefs.height(this)) + dp(10)
        root.addView(panel, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            topMargin = top; leftMargin = dp(10); rightMargin = dp(10)
        })

        // En-tete
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        head.addView(LinearLayout(this).apply {
            addView(plated(text("Wave Drop", 20f, Color.WHITE, bold = true)))
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        head.addView(ImageView(this).apply {
            setImageDrawable(LineIcon(LineIcon.Kind.CLOSE, Color.WHITE, d))
            setPadding(dp(9), dp(9), dp(9), dp(9))
            background = Glass.pressable(GlassPanel.plate(this@WaveDropActivity, 18f), dpf(18))
            setOnClickListener { close() }
        }, LinearLayout.LayoutParams(dp(36), dp(36)))
        panel.addView(head)
        body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        panel.addView(body)
        status = text("", 13f, Color.WHITE).apply { visibility = View.GONE }
        panel.addView(plated(status), LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) })
        bar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 1000
            progressTintList = android.content.res.ColorStateList.valueOf(Glass.ACCENT)
            progressBackgroundTintList = android.content.res.ColorStateList.valueOf(Color.argb(40, 255, 255, 255))
            visibility = View.GONE
        }
        panel.addView(bar, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8)).apply { topMargin = dp(8) })
        setContentView(root)

        handleIntent(intent)
        dropIn()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    private fun handleIntent(i: Intent?) {
        val id = i?.getIntExtra(EXTRA_REQUEST, 0) ?: 0
        val r = if (id != 0) WaveDrop.requests[id] else null
        if (r != null) showRequest(r)
        else if (WaveDrop.outgoing != null) showTargets()
        else showCategories()
    }

    // ================== Animation : le panneau coule depuis l'ilot ==================

    private fun dropIn() {
        panel.post {
            val w = panel.width.toFloat().coerceAtLeast(1f)
            val islandW = dpf(Prefs.width(this)) + dpf(40)
            panel.pivotX = w / 2f
            panel.pivotY = 0f
            panel.scaleX = (islandW / w).coerceIn(0.2f, 1f)
            panel.scaleY = 0.08f
            panel.translationY = -dpf(14)
            body.alpha = 0f
            val s = Spring.OPEN
            ValueAnimator.ofFloat(0f, 1f).apply {
                duration = s.durationMs
                interpolator = s
                val sx = panel.scaleX
                addUpdateListener {
                    val f = it.animatedValue as Float
                    panel.scaleX = sx + (1f - sx) * f
                    panel.scaleY = 0.08f + 0.92f * f
                    panel.translationY = -dpf(14) * (1f - f)
                }
                start()
            }
            body.animate().alpha(1f).setStartDelay(140).setDuration(200).start()
        }
    }

    private fun close() {
        if (closing) return
        closing = true
        val w = panel.width.toFloat().coerceAtLeast(1f)
        val islandW = dpf(Prefs.width(this)) + dpf(20)
        val s = Spring.CLOSE
        body.animate().alpha(0f).setDuration(90).start()
        ValueAnimator.ofFloat(0f, 1f).apply {
            duration = (s.durationMs * 0.7).toLong()
            interpolator = s
            val tx = (islandW / w).coerceIn(0.2f, 1f)
            addUpdateListener {
                val f = it.animatedValue as Float
                panel.scaleX = 1f + (tx - 1f) * f
                panel.scaleY = 1f - 0.94f * f
                panel.alpha = 1f - 0.6f * f
            }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: android.animation.Animator) {
                    finish()
                    overridePendingTransition(0, 0)
                }
            })
            start()
        }
    }

    override fun onBackPressed() = close()

    // ================== 1. Quoi envoyer ==================

    private class Category(val icon: LineIcon.Kind, val label: String, val types: Array<String>, val media: String? = null)

    private val categories = listOf(
        Category(LineIcon.Kind.FOLDER, "Fichiers", arrayOf("*/*")),
        Category(LineIcon.Kind.IMAGE, "Images", arrayOf("image/*"), "image/*"),
        Category(LineIcon.Kind.VIDEO, "Vidéos", arrayOf("video/*"), "video/*"),
        Category(LineIcon.Kind.MUSIC, "Musique", arrayOf("audio/*")),
        Category(LineIcon.Kind.DOCUMENT, "Documents", arrayOf("application/pdf", "text/*", "application/msword", "application/vnd.*")),
        Category(LineIcon.Kind.APPS, "Applis", arrayOf("application/vnd.android.package-archive")),
    )

    private fun showCategories() {
        body.removeAllViews()
        body.addView(plated(text("Que veux-tu envoyer ?", 14f, Color.WHITE)), LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12); bottomMargin = dp(6) })
        var row: LinearLayout? = null
        categories.forEachIndexed { i, c ->
            if (i % 3 == 0) {
                row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
                body.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(96)).apply { topMargin = dp(6) })
            }
            val tile = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER
                background = Glass.pressable(GlassPanel.plate(this@WaveDropActivity, 22f), dpf(22))
                isClickable = true
                setOnClickListener { pick(c) }
                addView(ImageView(this@WaveDropActivity).apply { setImageDrawable(LineIcon(c.icon, Color.WHITE, d)) }, LinearLayout.LayoutParams(dp(30), dp(30)))
                addView(text(c.label, 13f, Color.WHITE).apply { gravity = Gravity.CENTER; setPadding(0, dp(6), 0, 0) })
            }
            row!!.addView(tile, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f).apply {
                marginStart = if (i % 3 == 0) 0 else dp(8)
            })
            // Les tuiles arrivent l'une apres l'autre avec un rebond
            tile.scaleX = 0.4f; tile.scaleY = 0.4f; tile.alpha = 0f
            val pop = Spring.POP
            tile.animate().scaleX(1f).scaleY(1f).alpha(1f).setStartDelay(120L + i * 45L).setDuration(pop.durationMs).setInterpolator(pop).start()
        }
        if (!WaveDrop.enabled(this)) {
            body.addView(plated(text("Wave Drop est désactivé : active-le dans l'onglet Wave Drop de l'appli pour être visible des autres appareils.", 13f, Glass.WARN)),
                LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) })
        }
    }

    private fun pick(c: Category) {
        val i = if (c.media != null && Build.VERSION.SDK_INT >= 33) {
            Intent(MediaStore.ACTION_PICK_IMAGES).apply { type = c.media }
        } else {
            Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
                addCategory(Intent.CATEGORY_OPENABLE)
                type = if (c.types.size == 1) c.types[0] else "*/*"
                if (c.types.size > 1) putExtra(Intent.EXTRA_MIME_TYPES, c.types)
            }
        }
        try { startActivityForResult(i, PICK) } catch (e: Exception) {
            startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).apply { addCategory(Intent.CATEGORY_OPENABLE); type = "*/*" }, PICK)
        }
    }

    @Deprecated("API Activity")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != PICK || resultCode != RESULT_OK) return
        val uri = data?.data ?: return
        // Garde l'acces au fichier meme si le panneau se ferme pendant l'envoi
        try { contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) } catch (e: Exception) { }
        WaveDrop.outgoing = WaveDrop.describe(this, uri)
        Log.i("LiquidIsland", "Wave Drop : fichier choisi ${WaveDrop.outgoing?.name}")
        showTargets()
    }

    // ================== 2. A qui l'envoyer ==================

    private fun showTargets() {
        val out = WaveDrop.outgoing ?: return showCategories()
        body.removeAllViews()
        // Le fichier choisi
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = GlassPanel.plate(this@WaveDropActivity, 22f, strong = true)
            setPadding(dp(12), dp(12), dp(12), dp(12))
        }
        val thumb = ImageView(this).apply {
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            background = GradientDrawable().apply { cornerRadius = dpf(12); setColor(Color.argb(40, 255, 255, 255)) }
            clipToOutline = true
            setImageDrawable(LineIcon(kindOf(out.mime), Color.WHITE, d))
            setPadding(dp(12), dp(12), dp(12), dp(12))
        }
        if (Build.VERSION.SDK_INT >= 29) {
            Thread {
                val bmp = try { contentResolver.loadThumbnail(out.uri, Size(160, 160), null) } catch (e: Exception) { null }
                if (bmp != null) runOnUiThread { thumb.setPadding(0, 0, 0, 0); thumb.scaleType = ImageView.ScaleType.CENTER_CROP; thumb.setImageBitmap(bmp) }
            }.start()
        }
        card.addView(thumb, LinearLayout.LayoutParams(dp(52), dp(52)).apply { marginEnd = dp(12) })
        val names = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        names.addView(text(out.name, 15f, Color.WHITE, bold = true).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.MIDDLE })
        names.addView(text(size(out.size) + " · " + out.mime.substringBefore('/'), 12f, Glass.INK_SOFT))
        card.addView(names, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        card.addView(text("Changer", 13f, Glass.ACCENT).apply {
            setPadding(dp(10), dp(6), dp(10), dp(6))
            setOnClickListener { WaveDrop.outgoing = null; showCategories() }
        })
        body.addView(card, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10) })

        // Ondes : on cherche
        val radar = Radar(this)
        body.addView(radar, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(96)).apply { topMargin = dp(8) })
        body.addView(plated(text(
            if (nfc?.isEnabled == true) "Colle l'autre téléphone au dos du tien (NFC) ou rapproche l'appareil."
            else "Rapproche l'autre appareil (téléphone ou PC avec Wave Island).",
            13f, Color.WHITE
        ).apply { gravity = Gravity.CENTER }), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(6) })
        nearbyBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        body.addView(nearbyBox)
        nearbyChanged()
    }

    override fun nearbyChanged() {
        val box = nearbyBox ?: return
        if (WaveDrop.outgoing == null || request != null) return
        box.removeAllViews()
        val list = WaveDrop.nearbyList()
        if (list.isEmpty()) {
            box.addView(plated(text("Aucun appareil Wave Drop autour pour l'instant…", 13f, Color.WHITE).apply { gravity = Gravity.CENTER }))
            return
        }
        list.take(5).forEachIndexed { i, n ->
            val best = i == 0 && n.close
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                background = GlassPanel.plate(this@WaveDropActivity, 20f, strong = best)
                setPadding(dp(14), dp(10), dp(10), dp(10))
            }
            row.addView(ImageView(this).apply { setImageDrawable(LineIcon(if (n.beacon.isPc) LineIcon.Kind.LAPTOP else LineIcon.Kind.PHONE, Color.WHITE, d)) },
                LinearLayout.LayoutParams(dp(26), dp(26)).apply { marginEnd = dp(12) })
            val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            col.addView(text(n.label, 15f, Color.WHITE, bold = true).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END })
            col.addView(text(distance(n), 12f, Color.argb(210, 255, 255, 255)))
            row.addView(col, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            row.addView(text("Envoyer", 14f, Glass.ON_ACCENT, bold = true).apply {
                gravity = Gravity.CENTER
                setPadding(dp(16), dp(8), dp(16), dp(8))
                background = Glass.pressable(Glass.tinted(d, dpf(18)), dpf(18), dark = true)
                setOnClickListener { WaveDrop.send(n) }
            })
            box.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(8) })
        }
    }

    private fun distance(n: WaveDrop.Nearby) = when {
        n.viaNfc -> "Collé à ton téléphone"
        n.rssi >= WaveDrop.CLOSE_RSSI -> "Tout près" + via(n)
        n.rssi >= -72 -> "Proche" + via(n)
        else -> "Dans les environs" + via(n)
    }

    private fun via(n: WaveDrop.Nearby) = if (n.beacon.ip != null) " · Wi-Fi" else " · Bluetooth"

    // ================== Demande recue ==================

    private fun showRequest(r: WaveDrop.Request) {
        request = r
        body.removeAllViews()
        body.addView(plated(text("${r.from} veut t'envoyer", 15f, Color.WHITE)), LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12); bottomMargin = dp(8) })
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = GlassPanel.plate(this@WaveDropActivity, 22f, strong = true)
            setPadding(dp(16), dp(14), dp(16), dp(14))
        }
        val line = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        line.addView(ImageView(this).apply { setImageDrawable(LineIcon(kindOf(r.offer.mime), Color.WHITE, d)) }, LinearLayout.LayoutParams(dp(28), dp(28)).apply { marginEnd = dp(12) })
        line.addView(text(r.offer.name, 16f, Color.WHITE, bold = true).apply { maxLines = 2; ellipsize = TextUtils.TruncateAt.MIDDLE })
        card.addView(line)
        card.addView(text(size(r.offer.size) + " · " + r.offer.mime, 12f, Color.argb(210, 255, 255, 255)).apply { setPadding(dp(40), dp(4), 0, 0) })
        body.addView(card)
        val buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setPadding(0, dp(14), 0, 0) }
        buttons.addView(button("Refuser", primary = false) { r.answer(false); close() }, LinearLayout.LayoutParams(0, dp(50), 1f))
        buttons.addView(button("Accepter", primary = true) {
            r.answer(true)
            buttons.visibility = View.GONE
            status.visibility = View.VISIBLE
            status.text = "Réception…"
            bar.visibility = View.VISIBLE
        }, LinearLayout.LayoutParams(0, dp(50), 1f).apply { marginStart = dp(10) })
        body.addView(buttons)
    }

    override fun request(r: WaveDrop.Request) { if (request == null && !closing) showRequest(r) }

    override fun progress(title: String, text: String, done: Long, total: Long) {
        status.visibility = View.VISIBLE
        status.text = if (total > 0) "$title · ${(done * 100 / total)} %" else title
        bar.visibility = View.VISIBLE
        bar.isIndeterminate = total <= 0
        if (total > 0) bar.progress = (done * 1000 / total).toInt()
    }

    override fun finished(title: String, text: String, ok: Boolean, received: Uri?, mime: String?) {
        status.visibility = View.VISIBLE
        status.text = (if (ok) "✓ " else "✕ ") + title + " — " + text
        status.setTextColor(if (ok) Glass.OK else Glass.WARN)
        bar.visibility = View.GONE
        if (ok && received != null) {
            body.addView(button("Ouvrir", primary = true) { openFile(this, received, mime) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48)).apply { topMargin = dp(12) })
        } else if (ok) panel.postDelayed({ close() }, 1600)
    }

    // ================== NFC : coller les telephones ==================

    override fun onResume() {
        super.onResume()
        WaveDrop.uis.add(this)
        WaveDrop.fastScan = true
        val a = nfc
        if (a != null && a.isEnabled && Build.VERSION.SDK_INT >= 19) {
            try {
                a.enableReaderMode(this, { tag ->
                    val iso = IsoDep.get(tag) ?: return@enableReaderMode
                    try {
                        iso.connect()
                        iso.timeout = 2000
                        val res = WaveDropHce.parse(iso.transceive(WaveDropHce.selectApdu()))
                        if (res != null) runOnUiThread {
                            WaveDrop.nfcPeer(res.first, res.second)
                            window.decorView.performHapticFeedback(android.view.HapticFeedbackConstants.CONFIRM)
                        }
                    } catch (e: Exception) {
                        Log.w("LiquidIsland", "Wave Drop : lecture NFC impossible ($e)")
                    } finally { try { iso.close() } catch (e: Exception) { } }
                }, NfcAdapter.FLAG_READER_NFC_A or NfcAdapter.FLAG_READER_NFC_B or NfcAdapter.FLAG_READER_SKIP_NDEF_CHECK or NfcAdapter.FLAG_READER_NO_PLATFORM_SOUNDS, null)
            } catch (e: Exception) { Log.w("LiquidIsland", "Wave Drop : NFC indisponible ($e)") }
        }
        handler.post(tick)
    }

    override fun onPause() {
        super.onPause()
        try { nfc?.disableReaderMode(this) } catch (e: Exception) { }
        WaveDrop.fastScan = false
        handler.removeCallbacks(tick)
    }

    override fun onDestroy() {
        WaveDrop.uis.remove(this)
        // Fenetre fermee sans repondre : on refuse
        request?.let { if (!it.accepted) it.answer(false) }
        super.onDestroy()
    }

    private val handler = android.os.Handler(android.os.Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() { nearbyChanged(); handler.postDelayed(this, 1500) }
    }

    // ================== Outils ==================

    /** Texte (ou vue) pose sur un petit carre translucide : lisible sur n'importe quel fond. */
    private fun plated(v: View): View {
        v.background = GlassPanel.plate(this, 14f)
        v.setPadding(dp(12), dp(6), dp(12), dp(7))
        return v
    }

    private fun kindOf(mime: String) = when {
        mime.startsWith("image") -> LineIcon.Kind.IMAGE
        mime.startsWith("video") -> LineIcon.Kind.VIDEO
        mime.startsWith("audio") -> LineIcon.Kind.MUSIC
        mime.contains("android.package") -> LineIcon.Kind.APPS
        mime.startsWith("text") || mime.contains("pdf") || mime.contains("document") -> LineIcon.Kind.DOCUMENT
        else -> LineIcon.Kind.FOLDER
    }

    private fun text(s: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    }

    private fun button(label: String, primary: Boolean, action: () -> Unit) = text(label, 15f, if (primary) Glass.ON_ACCENT else Glass.INK, bold = true).apply {
        gravity = Gravity.CENTER
        val r = dpf(25)
        background = if (primary) Glass.pressable(Glass.tinted(d, r), r, dark = true) else Glass.pressable(GlassPanel.plate(this@WaveDropActivity, 25f, strong = true), r)
        isClickable = true
        setOnClickListener { action() }
    }

    /** Ondes qui partent du centre (on cherche les appareils). */
    private class Radar(c: Context) : View(c) {
        private val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; color = Glass.ACCENT }
        private var t = 0f
        override fun onDraw(c: Canvas) {
            val cx = width / 2f
            val cy = height / 2f
            val maxR = height / 2f
            p.strokeWidth = resources.displayMetrics.density * 2f
            for (i in 0 until 3) {
                val f = (t + i / 3f) % 1f
                p.alpha = ((1f - f) * 200).toInt()
                c.drawCircle(cx, cy, maxR * (0.15f + 0.85f * f), p)
            }
            p.style = Paint.Style.FILL
            p.alpha = 255
            c.drawCircle(cx, cy, resources.displayMetrics.density * 6f, p)
            p.style = Paint.Style.STROKE
            t = (t + 0.012f) % 1f
            if (isShown) postInvalidateOnAnimation()
        }
    }

    companion object {
        const val EXTRA_REQUEST = "request"
        private const val PICK = 41

        fun size(b: Long): String = when {
            b < 0 -> "taille inconnue"
            b >= 1L shl 30 -> String.format("%.1f Go", b / (1024.0 * 1024 * 1024))
            b >= 1L shl 20 -> String.format("%.1f Mo", b / (1024.0 * 1024))
            else -> String.format("%d Ko", max(1, (b / 1024).toInt()))
        }

        fun icon(mime: String) = when {
            mime.startsWith("image") -> "🖼️"
            mime.startsWith("video") -> "🎬"
            mime.startsWith("audio") -> "🎵"
            mime.contains("android.package") -> "📦"
            mime.startsWith("text") || mime.contains("pdf") || mime.contains("document") -> "📄"
            else -> "📁"
        }

        fun openFile(c: Context, uri: Uri, mime: String?) {
            try {
                c.startActivity(Intent(Intent.ACTION_VIEW).apply {
                    setDataAndType(uri, mime ?: "*/*")
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                })
            } catch (e: Exception) { Log.w("LiquidIsland", "Wave Drop : ouverture impossible", e) }
        }

        fun open(c: Context, requestId: Int = 0) {
            c.startActivity(Intent(c, WaveDropActivity::class.java).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                if (requestId != 0) putExtra(EXTRA_REQUEST, requestId)
            })
        }
    }
}
