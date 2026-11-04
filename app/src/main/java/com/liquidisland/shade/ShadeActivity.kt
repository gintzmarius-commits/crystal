package com.liquidisland.shade

import android.app.Activity
import android.app.ActivityOptions
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.AudioManager
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.AlarmClock
import android.provider.MediaStore
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.text.TextUtils
import android.util.Log
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.liquidisland.Glass
import com.liquidisland.GlassPanel
import com.liquidisland.IslandAccessibility
import com.liquidisland.LineIcon
import com.liquidisland.MainActivity
import com.liquidisland.NotifListener
import com.liquidisland.Spring
import com.liquidisland.drop.WaveDrop
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.abs
import kotlin.math.pow

/**
 * Centre de notifications (tirer depuis le haut a gauche) et centre de controle (a droite) de Wave Island.
 * Ils remplacent le volet d'Android par du verre liquide : l'ecran est floute derriere, chaque carte et
 * chaque bouton est une plaque de verre avec ses reflets.
 */
class ShadeActivity : Activity() {

    companion object {
        const val NOTIFS = "notifs"
        const val CONTROLS = "controls"
        private const val EXTRA_MODE = "mode"
        private const val TAG = "LiquidIsland"

        fun open(c: Context, mode: String) {
            try {
                c.startActivity(Intent(c, ShadeActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    .putExtra(EXTRA_MODE, mode))
            } catch (e: Exception) { Log.w(TAG, "Centre impossible a ouvrir : $e") }
        }
    }

    private val d get() = resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private fun dpf(v: Int) = v * d

    private val main = Handler(Looper.getMainLooper())
    private lateinit var sheet: LinearLayout
    private lateinit var content: FrameLayout
    private lateinit var tabNotifs: TextView
    private lateinit var tabControls: TextView
    private var mode = NOTIFS
    private var closing = false
    private val notifListener: () -> Unit = { if (mode == NOTIFS && !closing) buildNotifs() }
    private val ticker = object : Runnable {
        override fun run() {
            if (mode == CONTROLS) refreshControls()
            main.postDelayed(this, 1500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS)
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        if (Build.VERSION.SDK_INT >= 28) window.attributes = window.attributes.apply {
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        // Fond : l'ecran floute (verre depoli) ; sans flou disponible, un voile sombre
        val blur = Build.VERSION.SDK_INT >= 31 && windowManager.isCrossWindowBlurEnabled
        window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
        window.attributes = window.attributes.apply {
            dimAmount = if (blur) 0.28f else 0.72f
            if (blur && Build.VERSION.SDK_INT >= 31) blurBehindRadius = dp(34)
        }
        if (blur && Build.VERSION.SDK_INT >= 31) window.addFlags(WindowManager.LayoutParams.FLAG_BLUR_BEHIND)
        Log.i(TAG, "Centre Wave Island ouvert (flou=$blur)")

        val root = FrameLayout(this).apply {
            setOnClickListener { close() }
        }
        sheet = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), statusBar() + dp(14), dp(14), dp(18))
        }
        root.addView(sheet, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // Onglets : Notifications | Controle
        val tabs = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        tabNotifs = tab("Notifications", LineIcon.Kind.BELL) { show(NOTIFS) }
        tabControls = tab("Contrôle", LineIcon.Kind.SETTINGS) { show(CONTROLS) }
        tabs.addView(tabNotifs)
        tabs.addView(tabControls, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(10) })
        sheet.addView(tabs, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        content = FrameLayout(this)
        sheet.addView(content, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f).apply { topMargin = dp(14) })

        // Petite barre en bas : glisser vers le haut referme
        sheet.addView(View(this).apply {
            background = GradientDrawable().apply { cornerRadius = dpf(3); setColor(Color.argb(150, 255, 255, 255)) }
        }, LinearLayout.LayoutParams(dp(120), dp(5)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(10) })

        setContentView(root)
        show(intent?.getStringExtra(EXTRA_MODE) ?: NOTIFS, first = true)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        closing = false
        sheet.alpha = 1f
        sheet.translationY = 0f
        show(intent.getStringExtra(EXTRA_MODE) ?: mode, first = true)
    }

    override fun onResume() {
        super.onResume()
        NotifListener.listeners.add(notifListener)
        main.post(ticker)
    }

    override fun onPause() {
        super.onPause()
        NotifListener.listeners.remove(notifListener)
        main.removeCallbacks(ticker)
    }

    override fun onDestroy() {
        torchCallback?.let { try { (getSystemService(CAMERA_SERVICE) as CameraManager).unregisterTorchCallback(it) } catch (e: Exception) { } }
        super.onDestroy()
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() = close()

    // Glisser vers le haut (depuis la moitie basse) referme, comme le centre de controle de l'iPhone
    private var downX = 0f
    private var downY = 0f
    override fun dispatchTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> { downX = e.rawX; downY = e.rawY }
            MotionEvent.ACTION_UP -> {
                val dy = e.rawY - downY
                if (dy < -dpf(110) && abs(dy) > abs(e.rawX - downX) * 1.5f && downY > resources.displayMetrics.heightPixels * 0.55f) {
                    close()
                    return true
                }
            }
        }
        return super.dispatchTouchEvent(e)
    }

    private fun statusBar(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dp(28)
    }

    // ================== Onglets et animation ==================

    private fun tab(label: String, kind: LineIcon.Kind, onClick: () -> Unit) = TextView(this).apply {
        text = label
        textSize = 14f
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(14), dp(8), dp(16), dp(8))
        compoundDrawablePadding = dp(8)
        tag = kind
        setOnClickListener { performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK); onClick() }
    }

    private fun styleTab(t: TextView, on: Boolean) {
        val ink = if (on) Color.rgb(24, 28, 36) else Color.WHITE
        t.setTextColor(ink)
        t.background = GradientDrawable().apply {
            cornerRadius = dpf(20)
            setColor(if (on) Color.argb(235, 255, 255, 255) else Color.argb(70, 20, 24, 30))
            setStroke(dp(1).coerceAtLeast(1), Color.argb(if (on) 255 else 70, 255, 255, 255))
        }
        t.setCompoundDrawables(LineIcon(t.tag as LineIcon.Kind, ink, d).apply { setBounds(0, 0, dp(18), dp(18)) }, null, null, null)
    }

    private fun show(m: String, first: Boolean = false) {
        if (m == mode && !first) return
        val fromRight = m == CONTROLS
        mode = m
        styleTab(tabNotifs, m == NOTIFS)
        styleTab(tabControls, m == CONTROLS)
        if (m == NOTIFS) buildNotifs() else buildControls()
        if (first) {
            // Le centre descend du haut de l'ecran, avec un rebond
            sheet.translationY = -dpf(60)
            sheet.alpha = 0f
            val s = Spring.OPEN
            sheet.animate().translationY(0f).setInterpolator(s).setDuration(s.durationMs).start()
            sheet.animate().alpha(1f).setDuration(160).start()
        } else {
            content.translationX = if (fromRight) dpf(60) else -dpf(60)
            content.alpha = 0f
            val s = Spring.SOFT
            content.animate().translationX(0f).alpha(1f).setInterpolator(s).setDuration(s.durationMs).start()
        }
    }

    private fun close() {
        if (closing) return
        closing = true
        val s = Spring.CLOSE
        sheet.animate().translationY(-dpf(80)).alpha(0f).setInterpolator(s).setDuration((s.durationMs * 0.6).toLong())
            .withEndAction { finish(); overridePendingTransition(0, 0) }.start()
    }

    // ================== Outils ==================

    private fun text(s: CharSequence, size: Float, color: Int = Color.WHITE, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        setShadowLayer(dpf(6), 0f, dpf(1), Color.argb(90, 0, 0, 0))
    }

    private fun glass(radius: Float, fill: Int = Color.argb(70, 16, 20, 28)) = GlassPanel(this, radius).apply { this.fill = fill }

    private fun launch(i: Intent, closeAfter: Boolean = true) {
        try {
            startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            if (closeAfter) close()
        } catch (e: Exception) { Log.w(TAG, "Ouverture impossible : $e") }
    }

    private fun send(pi: PendingIntent?, pkg: String) {
        var ok = false
        if (pi != null) try {
            val o = ActivityOptions.makeBasic()
            if (Build.VERSION.SDK_INT >= 34) {
                @Suppress("DEPRECATION")
                o.setPendingIntentBackgroundActivityStartMode(ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
            }
            pi.send(this, 0, null, null, null, null, o.toBundle()); ok = true
        } catch (e: Exception) { }
        if (!ok) packageManager.getLaunchIntentForPackage(pkg)?.let { launch(it, false) }
    }

    // ================== Centre de notifications ==================

    private class Item(
        val key: String, val pkg: String, val title: String, val text: String, val time: Long,
        val intent: PendingIntent?, val autoCancel: Boolean, val clearable: Boolean, val kept: Boolean,
        val actions: List<Notification.Action>
    )

    private fun items(): List<Item> {
        val out = ArrayList<Item>()
        val l = NotifListener.instance
        val active = try { l?.activeNotifications } catch (e: Exception) { null } ?: emptyArray()
        val groupsWithChildren = active.filter { it.notification.flags and Notification.FLAG_GROUP_SUMMARY == 0 }.map { it.groupKey }.toSet()
        for (sbn in active) {
            val n = sbn.notification ?: continue
            if (sbn.packageName == packageName && sbn.isOngoing) continue
            if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0 && sbn.groupKey in groupsWithChildren) continue
            val ex = n.extras
            val title = (ex.getCharSequence(Notification.EXTRA_TITLE) ?: ex.getCharSequence(Notification.EXTRA_TITLE_BIG))?.toString().orEmpty()
            val body = (ex.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: ex.getCharSequence(Notification.EXTRA_TEXT)
                ?: ex.getCharSequence(Notification.EXTRA_SUB_TEXT))?.toString().orEmpty()
            if (title.isBlank() && body.isBlank()) continue
            out.add(Item(sbn.key, sbn.packageName, title, body, sbn.postTime, n.contentIntent,
                n.flags and Notification.FLAG_AUTO_CANCEL != 0, sbn.isClearable, false,
                n.actions?.filter { it.remoteInputs.isNullOrEmpty() && it.actionIntent != null && !it.title.isNullOrBlank() }?.take(3) ?: emptyList()))
        }
        // Celles que l'ilot a retirees du volet d'Android (option « seulement sur l'ilot »)
        val keys = out.map { it.key }.toSet()
        for (k in NotifListener.kept) if (k.key !in keys) out.add(Item(k.key, k.pkg, k.title, k.text, k.time, k.intent, k.autoCancel, true, true, emptyList()))
        out.sortByDescending { it.time }
        return out
    }

    private fun buildNotifs() {
        content.removeAllViews()
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val scroll = ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            clipToPadding = false
            addView(col)
        }
        content.addView(scroll, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // Heure et date, en grand
        col.addView(text(SimpleDateFormat("HH:mm", Locale.FRANCE).format(Date()), 58f, bold = true).apply {
            setPadding(dp(6), 0, 0, 0)
            typeface = Typeface.create("sans-serif-light", Typeface.NORMAL)
        })
        col.addView(text(SimpleDateFormat("EEEE d MMMM", Locale.FRANCE).format(Date()).replaceFirstChar { it.uppercase() }, 16f).apply {
            setPadding(dp(8), 0, 0, dp(14))
        })

        if (NotifListener.instance == null) {
            col.addView(emptyCard("Autorise l'accès aux notifications dans Wave Island pour les voir ici.") {
                launch(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
            })
            return
        }
        val list = items()
        if (list.isEmpty()) {
            col.addView(emptyCard("Aucune notification", null))
            return
        }
        // En-tete : nombre + « Tout effacer »
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(6), 0, 0, dp(8)) }
        head.addView(text("${list.size} notification${if (list.size > 1) "s" else ""}", 15f, bold = true), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        if (list.any { it.clearable }) head.addView(pill("Tout effacer") { clearAll(list, col) })
        col.addView(head)
        list.forEachIndexed { i, it -> col.addView(card(it, i), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10) }) }
    }

    private fun emptyCard(msg: String, onClick: (() -> Unit)?): View = glass(26f).apply {
        gravity = Gravity.CENTER
        setPadding(dp(18), dp(26), dp(18), dp(26))
        addView(ImageView(this@ShadeActivity).apply { setImageDrawable(LineIcon(LineIcon.Kind.BELL, Color.WHITE, d)) }, LinearLayout.LayoutParams(dp(30), dp(30)))
        addView(text(msg, 15f).apply { gravity = Gravity.CENTER; setPadding(0, dp(10), 0, 0) })
        if (onClick != null) { isClickable = true; setOnClickListener { onClick() } } else isClickable = true
    }

    private fun pill(label: String, onClick: () -> Unit) = text(label, 13f, bold = true).apply {
        setPadding(dp(14), dp(7), dp(14), dp(7))
        background = Glass.pressable(GradientDrawable().apply {
            cornerRadius = dpf(16); setColor(Color.argb(90, 20, 24, 30)); setStroke(1.coerceAtLeast(dp(1)), Color.argb(90, 255, 255, 255))
        }, dpf(16))
        setOnClickListener { performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK); onClick() }
    }

    private val labels = HashMap<String, String>()
    private fun appLabel(pkg: String) = labels.getOrPut(pkg) {
        try { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() } catch (e: Exception) { pkg }
    }
    private fun appIcon(pkg: String): Drawable? = try { packageManager.getApplicationIcon(pkg) } catch (e: Exception) { null }

    private fun ago(t: Long): String {
        val m = (System.currentTimeMillis() - t) / 60_000
        return when {
            m < 1 -> "maintenant"
            m < 60 -> "il y a $m min"
            m < 24 * 60 && Date(t).date == Date().date -> SimpleDateFormat("HH:mm", Locale.FRANCE).format(Date(t))
            else -> SimpleDateFormat("d MMM", Locale.FRANCE).format(Date(t))
        }
    }

    private fun card(it: Item, index: Int): View {
        val c = glass(24f)
        c.setPadding(dp(14), dp(12), dp(14), dp(14))
        val top = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        top.addView(ImageView(this).apply { setImageDrawable(appIcon(it.pkg)) }, LinearLayout.LayoutParams(dp(20), dp(20)).apply { marginEnd = dp(8) })
        top.addView(text(appLabel(it.pkg).uppercase(Locale.FRANCE), 11.5f, Color.argb(215, 255, 255, 255)).apply { letterSpacing = 0.04f; maxLines = 1 },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        top.addView(text(ago(it.time), 11.5f, Color.argb(190, 255, 255, 255)))
        c.addView(top)
        if (it.title.isNotBlank()) c.addView(text(it.title, 15.5f, bold = true).apply { maxLines = 2; ellipsize = TextUtils.TruncateAt.END; setPadding(0, dp(8), 0, 0) })
        if (it.text.isNotBlank()) c.addView(text(it.text, 14f, Color.argb(232, 255, 255, 255)).apply { maxLines = 5; ellipsize = TextUtils.TruncateAt.END; setPadding(0, dp(2), 0, 0) })
        if (it.actions.isNotEmpty()) {
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setPadding(0, dp(10), 0, 0) }
            it.actions.forEach { a ->
                row.addView(pill(a.title.toString()) { send(a.actionIntent, it.pkg); if (it.autoCancel) dismiss(it); close() },
                    LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginEnd = dp(8) })
            }
            c.addView(row)
        }
        swipeToDismiss(c, it)
        // Apparition en cascade avec rebond
        c.alpha = 0f
        c.translationY = dpf(18)
        val s = Spring.POP
        c.animate().alpha(1f).translationY(0f).setStartDelay(40L + index.coerceAtMost(8) * 30L).setInterpolator(s).setDuration(s.durationMs).start()
        return c
    }

    /** Toucher : ouvre ; glisser sur le cote : efface (comme sur iPhone). */
    private fun swipeToDismiss(v: View, it: Item) {
        val slop = android.view.ViewConfiguration.get(this).scaledTouchSlop
        var sx = 0f; var sy = 0f; var dragging = false; var moved = false
        v.setOnTouchListener { view, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { sx = e.rawX; sy = e.rawY; dragging = false; moved = false; view.animate().scaleX(0.98f).scaleY(0.98f).setDuration(90).start() }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - sx; val dy = e.rawY - sy
                    if (!dragging && !moved && abs(dx) > slop && abs(dx) > abs(dy)) { dragging = true; view.parent?.requestDisallowInterceptTouchEvent(true) }
                    if (!dragging && abs(dy) > slop) moved = true
                    if (dragging && it.clearable) { view.translationX = dx; view.alpha = 1f - (abs(dx) / view.width).coerceIn(0f, 0.8f) }
                    else if (dragging) view.translationX = dx * 0.25f
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    view.animate().scaleX(1f).scaleY(1f).setDuration(140).start()
                    val dx = e.rawX - sx
                    if (dragging && it.clearable && abs(dx) > view.width * 0.33f && e.actionMasked == MotionEvent.ACTION_UP) {
                        view.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
                        view.animate().translationX(if (dx > 0) view.width.toFloat() else -view.width.toFloat()).alpha(0f).setDuration(180)
                            .withEndAction { (view.parent as? ViewGroup)?.removeView(view); dismiss(it) }.start()
                    } else if (dragging || moved || e.actionMasked == MotionEvent.ACTION_CANCEL) {
                        val s = Spring.SOFT
                        view.animate().translationX(0f).alpha(1f).setInterpolator(s).setDuration(s.durationMs).start()
                    } else {
                        // Simple toucher : ouvre l'appli concernee
                        send(it.intent, it.pkg)
                        if (it.autoCancel) dismiss(it)
                        close()
                    }
                }
            }
            true
        }
    }

    private fun dismiss(it: Item) {
        NotifListener.kept.removeAll { k -> k.key == it.key }
        if (!it.kept) NotifListener.cancel(it.key) else NotifListener.changed()
    }

    private fun clearAll(list: List<Item>, col: LinearLayout) {
        var i = 0
        for (k in 0 until col.childCount) {
            val v = col.getChildAt(k)
            if (v is GlassPanel) v.animate().translationX(v.width.toFloat()).alpha(0f).setStartDelay(i++ * 35L).setDuration(200).start()
        }
        main.postDelayed({
            list.filter { it.clearable }.forEach { dismiss(it) }
            NotifListener.kept.clear()
            buildNotifs()
        }, 260L + i * 35L)
    }

    // ================== Centre de controle ==================

    private class Toggle(val view: GlassPanel, val icon: ImageView, val label: TextView?, val kind: LineIcon.Kind, val state: () -> Boolean, val round: Boolean, val onColor: Int)
    private val toggles = ArrayList<Toggle>()
    private var brightness: GlassSlider? = null
    private var volume: GlassSlider? = null
    private var mediaTitle: TextView? = null
    private var mediaArtist: TextView? = null
    private var mediaPlay: ImageView? = null
    private var torchOn = false
    private var torchCallback: CameraManager.TorchCallback? = null

    private fun buildControls() {
        content.removeAllViews()
        toggles.clear()
        val w = resources.displayMetrics.widthPixels - dp(28)
        val gap = dp(12)
        val cell = (w - 3 * gap) / 4
        val big = 2 * cell + gap
        val grid = FrameLayout(this)
        fun put(v: View, col: Int, row: Int, cw: Int, ch: Int) {
            grid.addView(v, FrameLayout.LayoutParams(cw, ch).apply { leftMargin = col * (cell + gap); topMargin = row * (cell + gap) })
        }

        // Bloc connexions (2 x 2) : Avion, Donnees, Wi-Fi, Bluetooth
        val net = glass(30f).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; isClickable = true }
        val r1 = LinearLayout(this).apply { gravity = Gravity.CENTER }
        val r2 = LinearLayout(this).apply { gravity = Gravity.CENTER }
        val rs = (cell * 0.66f).toInt()
        r1.addView(round(LineIcon.Kind.AIRPLANE, Color.rgb(255, 159, 10), rs, { airplane() }) { launch(Intent(Settings.ACTION_AIRPLANE_MODE_SETTINGS)) },
            LinearLayout.LayoutParams(rs, rs).apply { marginEnd = gap })
        r1.addView(round(LineIcon.Kind.DATA, Color.rgb(48, 209, 88), rs, { cellular() }) { internetPanel() }, LinearLayout.LayoutParams(rs, rs))
        r2.addView(round(LineIcon.Kind.WIFI, Color.rgb(10, 132, 255), rs, { wifi() }) { wifiTap() }, LinearLayout.LayoutParams(rs, rs).apply { marginEnd = gap })
        r2.addView(round(LineIcon.Kind.BLUETOOTH, Color.rgb(10, 132, 255), rs, { bluetooth() }) { bluetoothTap() }, LinearLayout.LayoutParams(rs, rs))
        net.addView(r1)
        net.addView(r2, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = gap })
        put(net, 0, 0, big, big)

        // Bloc musique (2 x 2)
        put(mediaBlock(big), 2, 0, big, big)

        // Rotation, Ne pas deranger, Lampe, Wave Drop + curseurs luminosite / volume
        put(tile(LineIcon.Kind.ROTATE, "Rotation", { rotationLocked() }, { toggleRotation() }) { launch(Intent(Settings.ACTION_DISPLAY_SETTINGS)) }, 0, 2, cell, cell)
        put(tile(LineIcon.Kind.MOON, "Ne pas déranger", { dnd() }, { toggleDnd() }) { launch(Intent("android.settings.ZEN_MODE_SETTINGS")) }, 1, 2, cell, cell)
        put(tile(LineIcon.Kind.TORCH, "Lampe", { torchOn }, { toggleTorch() }, null), 0, 3, cell, cell)
        put(tile(LineIcon.Kind.DROP, "Wave Drop", { WaveDrop.enabled(this) }, { toggleDrop() }) {
            launch(Intent(this, MainActivity::class.java).putExtra("ci", "drop"))
        }, 1, 3, cell, cell)
        brightness = GlassSlider(this, LineIcon.Kind.SUN, readBrightness()) { setBrightness(it) }
        put(brightness!!, 2, 2, cell, big)
        volume = GlassSlider(this, LineIcon.Kind.VOLUME, readVolume()) { setVolume(it) }
        put(volume!!, 3, 2, cell, big)

        // Raccourcis
        put(tile(LineIcon.Kind.SCREENSHOT, "Capture", null, { globalAction(9 /* GLOBAL_ACTION_TAKE_SCREENSHOT */, 550) }, null), 0, 4, cell, cell)
        put(tile(LineIcon.Kind.LOCK, "Verrouiller", null, { globalAction(8 /* GLOBAL_ACTION_LOCK_SCREEN */, 300) }, null), 1, 4, cell, cell)
        put(tile(LineIcon.Kind.CAMERA, "Photo", null, { launch(Intent(MediaStore.INTENT_ACTION_STILL_IMAGE_CAMERA)) }, null), 2, 4, cell, cell)
        put(tile(LineIcon.Kind.CLOCK, "Horloge", null, { launch(Intent(AlarmClock.ACTION_SHOW_ALARMS)) }, null), 3, 4, cell, cell)
        put(tile(LineIcon.Kind.CALCULATOR, "Calculatrice", null, {
            launch(Intent.makeMainSelectorActivity(Intent.ACTION_MAIN, Intent.CATEGORY_APP_CALCULATOR))
        }, null), 0, 5, cell, cell)
        put(tile(LineIcon.Kind.ISLAND, "Wave Island", null, { launch(Intent(this, MainActivity::class.java)) }, null), 1, 5, cell, cell)
        put(tile(LineIcon.Kind.SETTINGS, "Paramètres", null, { launch(Intent(Settings.ACTION_SETTINGS)) }, null), 2, 5, cell, cell)
        // Le volet d'Android reste accessible (pour ses reglages a lui)
        put(tile(LineIcon.Kind.APPS, "Volet Android", null, { globalAction(5 /* GLOBAL_ACTION_QUICK_SETTINGS */, 300) }, null), 3, 5, cell, cell)

        val scroll = ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            addView(grid)
        }
        content.addView(scroll, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        watchTorch()
        refreshControls()
        // Les blocs apparaissent en cascade avec un rebond
        for (i in 0 until grid.childCount) {
            val v = grid.getChildAt(i)
            v.scaleX = 0.85f; v.scaleY = 0.85f; v.alpha = 0f
            val s = Spring.POP
            v.animate().scaleX(1f).scaleY(1f).alpha(1f).setStartDelay(30L + i * 22L).setInterpolator(s).setDuration(s.durationMs).start()
        }
    }

    private fun press(v: View) {
        v.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
        v.animate().scaleX(0.9f).scaleY(0.9f).setDuration(70).withEndAction {
            val s = Spring.POP
            v.animate().scaleX(1f).scaleY(1f).setInterpolator(s).setDuration(s.durationMs).start()
        }.start()
    }

    /** Bouton rond du bloc connexions (couleur quand actif, comme sur iPhone). */
    private fun round(kind: LineIcon.Kind, color: Int, size: Int, state: () -> Boolean, onClick: () -> Unit): View {
        val p = glass(size / 2f / d, Color.argb(80, 255, 255, 255)).apply { gravity = Gravity.CENTER; isClickable = true }
        val ic = ImageView(this)
        p.addView(ic, LinearLayout.LayoutParams((size * 0.46f).toInt(), (size * 0.46f).toInt()))
        p.setOnClickListener { press(p); onClick(); main.postDelayed({ refreshControls() }, 400) }
        toggles.add(Toggle(p, ic, null, kind, state, true, color))
        return p
    }

    /** Petite plaque carree : icone + nom ; blanche quand activee. */
    private fun tile(kind: LineIcon.Kind, label: String, state: (() -> Boolean)?, onClick: () -> Unit, onLong: (() -> Unit)?): View {
        val p = glass(24f).apply { gravity = Gravity.CENTER; isClickable = true }
        val ic = ImageView(this).apply { setImageDrawable(LineIcon(kind, Color.WHITE, d)) }
        p.addView(ic, LinearLayout.LayoutParams(dp(26), dp(26)))
        val t = text(label, 10.5f).apply { gravity = Gravity.CENTER; maxLines = 1; ellipsize = TextUtils.TruncateAt.END; setPadding(dp(4), dp(5), dp(4), 0) }
        p.addView(t, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        p.setOnClickListener { press(p); onClick(); main.postDelayed({ refreshControls() }, 350) }
        if (onLong != null) p.setOnLongClickListener { p.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS); onLong(); true }
        if (state != null) toggles.add(Toggle(p, ic, t, kind, state, false, Color.WHITE))
        return p
    }

    private fun mediaBlock(size: Int): View {
        val p = glass(30f).apply { setPadding(dp(16), dp(16), dp(16), dp(12)); isClickable = true }
        mediaTitle = text("Aucune lecture", 15f, bold = true).apply { maxLines = 2; ellipsize = TextUtils.TruncateAt.END }
        mediaArtist = text("", 13f, Color.argb(205, 255, 255, 255)).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END }
        p.addView(mediaTitle)
        p.addView(mediaArtist)
        p.addView(View(this), LinearLayout.LayoutParams(1, 0, 1f))
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        fun btn(kind: LineIcon.Kind, s: Int, action: (MediaController.TransportControls, Boolean) -> Unit) = ImageView(this).apply {
            setImageDrawable(LineIcon(kind, Color.WHITE, d))
            setPadding(dp(6), dp(6), dp(6), dp(6))
            setOnClickListener { press(this); media()?.let { m -> action(m.transportControls, playing(m)) }; main.postDelayed({ refreshControls() }, 300) }
            row.addView(this, LinearLayout.LayoutParams(s, s))
        }
        btn(LineIcon.Kind.PREV, dp(40)) { t, _ -> t.skipToPrevious() }
        mediaPlay = btn(LineIcon.Kind.PLAY, dp(48)) { t, pl -> if (pl) t.pause() else t.play() }.apply {
            (layoutParams as LinearLayout.LayoutParams).apply { marginStart = dp(10); marginEnd = dp(10) }
        }
        btn(LineIcon.Kind.NEXT, dp(40)) { t, _ -> t.skipToNext() }
        p.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        p.setOnClickListener { media()?.sessionActivity?.let { send(it, media()?.packageName ?: packageName); close() } }
        return p
    }

    private fun refreshControls() {
        if (mode != CONTROLS) return
        for (t in toggles) {
            val on = try { t.state() } catch (e: Exception) { false }
            if (t.round) {
                t.view.fill = if (on) t.onColor else Color.argb(80, 255, 255, 255)
                t.icon.setImageDrawable(LineIcon(t.kind, Color.WHITE, d))
            } else {
                t.view.fill = if (on) Color.argb(236, 255, 255, 255) else Color.argb(70, 16, 20, 28)
                val ink = if (on) Color.rgb(24, 28, 36) else Color.WHITE
                t.icon.setImageDrawable(LineIcon(t.kind, ink, d))
                t.label?.setTextColor(ink)
                t.label?.setShadowLayer(if (on) 0f else dpf(6), 0f, dpf(1), Color.argb(90, 0, 0, 0))
            }
        }
        volume?.let { if (!it.dragging) it.value = readVolume() }
        val m = media()
        val md = m?.metadata
        mediaTitle?.text = md?.getString(android.media.MediaMetadata.METADATA_KEY_TITLE) ?: if (m != null) appLabel(m.packageName) else "Aucune lecture"
        mediaArtist?.text = md?.getString(android.media.MediaMetadata.METADATA_KEY_ARTIST) ?: ""
        mediaPlay?.setImageDrawable(LineIcon(if (m != null && playing(m)) LineIcon.Kind.PAUSE else LineIcon.Kind.PLAY, Color.WHITE, d))
    }

    // ---------- Etats et actions ----------

    private fun airplane() = Settings.Global.getInt(contentResolver, Settings.Global.AIRPLANE_MODE_ON, 0) == 1

    private fun cellular(): Boolean = try {
        val cm = getSystemService(CONNECTIVITY_SERVICE) as ConnectivityManager
        cm.getNetworkCapabilities(cm.activeNetwork)?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) == true
    } catch (e: Exception) { false }

    private fun wifi() = try { (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager).isWifiEnabled } catch (e: Exception) { false }

    private fun bluetooth() = try { (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager).adapter?.isEnabled == true } catch (e: Exception) { false }

    private fun internetPanel() {
        if (Build.VERSION.SDK_INT >= 29) launch(Intent(Settings.Panel.ACTION_INTERNET_CONNECTIVITY), false)
        else launch(Intent(Settings.ACTION_WIRELESS_SETTINGS), false)
    }

    @Suppress("DEPRECATION")
    private fun wifiTap() {
        // Android 10+ n'autorise plus les applis a couper le Wi-Fi : on ouvre le petit panneau du systeme
        if (Build.VERSION.SDK_INT >= 29) launch(Intent(Settings.Panel.ACTION_WIFI), false)
        else try { (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager).isWifiEnabled = !wifi() } catch (e: Exception) { }
    }

    @Suppress("DEPRECATION", "MissingPermission")
    private fun bluetoothTap() {
        try {
            val a = (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager).adapter ?: return
            if (Build.VERSION.SDK_INT < 33) { if (a.isEnabled) a.disable() else a.enable(); return }
            if (!a.isEnabled) { launch(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE), false); return }
        } catch (e: Exception) { }
        launch(Intent(Settings.ACTION_BLUETOOTH_SETTINGS), false)
    }

    private fun needWrite(): Boolean {
        if (Settings.System.canWrite(this)) return false
        launch(Intent(Settings.ACTION_MANAGE_WRITE_SETTINGS, Uri.parse("package:$packageName")), false)
        return true
    }

    private fun rotationLocked() = Settings.System.getInt(contentResolver, Settings.System.ACCELEROMETER_ROTATION, 1) == 0

    private fun toggleRotation() {
        if (needWrite()) return
        try { Settings.System.putInt(contentResolver, Settings.System.ACCELEROMETER_ROTATION, if (rotationLocked()) 1 else 0) } catch (e: Exception) { }
    }

    private fun dnd(): Boolean {
        val f = NotifListener.instance?.currentInterruptionFilter
            ?: (getSystemService(NOTIFICATION_SERVICE) as NotificationManager).currentInterruptionFilter
        return f != NotificationManager.INTERRUPTION_FILTER_ALL && f != NotificationManager.INTERRUPTION_FILTER_UNKNOWN
    }

    private fun toggleDnd() {
        val want = if (dnd()) NotificationListenerService.INTERRUPTION_FILTER_ALL else NotificationListenerService.INTERRUPTION_FILTER_PRIORITY
        val l = NotifListener.instance
        try {
            if (l != null) { l.requestInterruptionFilter(want); return }
        } catch (e: Exception) { }
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (nm.isNotificationPolicyAccessGranted) nm.setInterruptionFilter(want)
        else launch(Intent(Settings.ACTION_NOTIFICATION_POLICY_ACCESS_SETTINGS), false)
    }

    private fun torchId(): String? = try {
        val cm = getSystemService(CAMERA_SERVICE) as CameraManager
        cm.cameraIdList.firstOrNull { cm.getCameraCharacteristics(it).get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true }
    } catch (e: Exception) { null }

    private fun watchTorch() {
        if (torchCallback != null) return
        val cb = object : CameraManager.TorchCallback() {
            override fun onTorchModeChanged(cameraId: String, enabled: Boolean) {
                if (cameraId == torchId()) { torchOn = enabled; refreshControls() }
            }
        }
        torchCallback = cb
        try { (getSystemService(CAMERA_SERVICE) as CameraManager).registerTorchCallback(cb, main) } catch (e: Exception) { }
    }

    private fun toggleTorch() {
        val id = torchId() ?: return
        try { (getSystemService(CAMERA_SERVICE) as CameraManager).setTorchMode(id, !torchOn); torchOn = !torchOn } catch (e: Exception) { }
    }

    private fun toggleDrop() {
        WaveDrop.init(this)
        val on = !WaveDrop.enabled(this)
        if (on && !WaveDrop.hasPermissions(this)) { launch(Intent(this, MainActivity::class.java).putExtra("ci", "drop")); return }
        WaveDrop.setEnabled(this, on)
        if (on) WaveDrop.restart(this) else WaveDrop.stop()
    }

    private fun globalAction(action: Int, delay: Long) {
        val acc = IslandAccessibility.instance ?: return
        close()
        main.postDelayed({ try { acc.performGlobalAction(action) } catch (e: Exception) { } }, delay)
    }

    // Luminosite : la valeur du systeme n'est pas lineaire a l'oeil, on passe par une courbe (comme Android)
    private fun maxBrightness(): Int {
        val id = resources.getIdentifier("config_screenBrightnessSettingMaximum", "integer", "android")
        return (if (id > 0) try { resources.getInteger(id) } catch (e: Exception) { 255 } else 255).coerceAtLeast(1)
    }

    private fun readBrightness(): Float {
        val v = Settings.System.getInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, 128)
        return (v.toFloat() / maxBrightness()).coerceIn(0f, 1f).pow(1f / 2.2f)
    }

    private var askedWrite = false
    private fun setBrightness(f: Float) {
        // Effet immediat sur cette fenetre, meme avant l'autorisation
        window.attributes = window.attributes.apply { screenBrightness = f.coerceAtLeast(0.02f).pow(2.2f) }
        if (!Settings.System.canWrite(this)) {
            if (!askedWrite) { askedWrite = true; needWrite() }
            return
        }
        try {
            Settings.System.putInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, (f.pow(2.2f) * maxBrightness()).toInt().coerceAtLeast(1))
        } catch (e: Exception) { }
    }

    private fun audio() = getSystemService(AUDIO_SERVICE) as AudioManager
    private fun readVolume(): Float {
        val a = audio()
        return a.getStreamVolume(AudioManager.STREAM_MUSIC).toFloat() / a.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
    }

    private fun setVolume(f: Float) {
        val a = audio()
        val max = a.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        val v = Math.round(f * max)
        if (v != a.getStreamVolume(AudioManager.STREAM_MUSIC)) try { a.setStreamVolume(AudioManager.STREAM_MUSIC, v, 0) } catch (e: Exception) { }
    }

    private fun media(): MediaController? = try {
        val msm = getSystemService(MEDIA_SESSION_SERVICE) as MediaSessionManager
        val all = msm.getActiveSessions(ComponentName(this, NotifListener::class.java))
        all.firstOrNull { playing(it) } ?: all.firstOrNull()
    } catch (e: Exception) { null }

    private fun playing(m: MediaController) = m.playbackState?.state == PlaybackState.STATE_PLAYING
}
