package com.liquidisland

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.ColorDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast

class MainActivity : Activity() {

    private val d get() = resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private fun dpf(v: Int) = v * d
    private val ui = Handler(Looper.getMainLooper())

    private lateinit var heroStatus: TextView
    private lateinit var heroButton: TextView
    private lateinit var preview: IslandPreview
    private lateinit var segIsland: TextView
    private lateinit var segNotch: TextView
    private lateinit var offsetRow: View
    private lateinit var updateCard: LinearLayout
    private lateinit var updateText: TextView
    private lateinit var updateBar: ProgressBar
    private lateinit var updateButton: TextView
    private val rows = mutableListOf<PermRow>()

    private class PermRow(val chip: TextView, val check: () -> Boolean?)

    private val isXiaomi = Build.MANUFACTURER.lowercase().let { it.contains("xiaomi") || it.contains("redmi") || it.contains("poco") }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setupWindow()

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(70), dp(18), dp(40))
        }

        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        header.addView(android.widget.ImageView(this).apply {
            setImageDrawable(applicationInfo.loadIcon(packageManager))
        }, LinearLayout.LayoutParams(dp(52), dp(52)).apply { marginEnd = dp(12) })
        header.addView(text("Wave Island", 32f, Glass.INK, bold = true))
        col.addView(header)
        col.addView(text("Ton îlot dynamique en verre, toujours à l'écran.", 15f, Glass.INK_SOFT).apply {
            setPadding(0, dp(2), 0, dp(18))
        })

        col.addView(buildUpdateCard())
        col.addView(heroCard())

        // Apparence
        col.addView(section("Apparence"))
        col.addView(appearanceCard())

        // Ilot Xiaomi
        if (isXiaomi) {
            col.addView(section("Îlot de Xiaomi"))
            val x = card()
            x.addView(text(
                "HyperOS a sa propre « Super île ». Android ne laisse aucune appli la couper à ta place, " +
                    "il faut le faire une fois dans les Réglages :\n\n" +
                    "• Réglages › Écran › Super Island : désactive-la.\n" +
                    "• Si elle revient pour une appli : Réglages › Notifications et Centre de contrôle › l'appli › Super Island.\n" +
                    "• Introuvable ? Tape « Super » dans la recherche des Réglages.",
                14f, Glass.INK_SOFT
            ).apply { setLineSpacing(0f, 1.15f) })
            x.addView(glassButton("Ouvrir les réglages Écran", primary = false) {
                open(Intent(Settings.ACTION_DISPLAY_SETTINGS))
            }, buttonLp(top = 14))
            col.addView(x)
        }

        // Autorisations
        col.addView(section("Autorisations"))
        val perms = card()
        perms.addView(permRow("1", "Afficher par-dessus les applis", "Pour dessiner l'îlot sur l'écran", {
            Settings.canDrawOverlays(this)
        }) { open(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))) })
        perms.addView(divider())
        perms.addView(permRow("2", "Accès aux notifications", "Pour afficher tes notifications dans l'îlot", {
            listenerEnabled()
        }) { openListenerSettings() })
        perms.addView(divider())
        perms.addView(permRow("3", "Batterie sans restriction", "Pour que l'îlot ne soit jamais coupé", {
            (getSystemService(Context.POWER_SERVICE) as PowerManager).isIgnoringBatteryOptimizations(packageName)
        }) { open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))) })
        perms.addView(divider())
        perms.addView(permRow("4", "Mises à jour automatiques", "Autoriser l'installation des nouvelles versions", {
            packageManager.canRequestPackageInstalls()
        }) { openInstallPermission() })
        if (isXiaomi) {
            perms.addView(divider())
            perms.addView(permRow("5", "Démarrage automatique", "Infos de l'appli › Démarrage auto", { null }) { openAppInfo() })
        }
        col.addView(perms)

        // Aide paramètres restreints
        col.addView(section("Accès aux notifications grisé ?"))
        val help = card()
        help.addView(text(
            "Android bloque ce réglage pour les applis installées hors Play Store. Pour le débloquer :\n\n" +
                "1. Touche « Accès aux notifications » ci-dessus et essaie d'activer Wave Island (un message « paramètre restreint » s'affiche).\n" +
                "2. Ouvre les infos de l'appli avec le bouton ci-dessous.\n" +
                "3. Touche le menu ⋮ en haut à droite, puis « Autoriser les paramètres restreints ».\n" +
                "4. Reviens ici et active l'accès aux notifications.",
            14f, Glass.INK_SOFT
        ).apply { setLineSpacing(0f, 1.15f) })
        help.addView(glassButton("Ouvrir les infos de l'appli", primary = false) { openAppInfo() }, buttonLp(top = 14))
        col.addView(help)

        // Options
        col.addView(section("Options"))
        val opts = card()
        opts.addView(switchRow(
            "Masquer les notifications du système",
            text("Seul l'îlot les affiche (elles quittent aussi le volet)", 13f, Glass.INK_SOFT),
            Prefs.hideSystem(this)
        ) { Prefs.setHideSystem(this, it) })
        opts.addView(divider())
        opts.addView(permRow("", "Notifications pop-up du système", "À couper pour ne garder que l'îlot", { null }) {
            openNotificationSettings()
        })
        col.addView(opts)

        col.addView(text("Version ${BuildConfig.VERSION_NAME}", 12f, Glass.INK_SOFT).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(24), 0, 0)
        })

        val scroll = ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            addView(col)
        }
        val root = FrameLayout(this).apply {
            background = Glass.background(resources.displayMetrics.widthPixels)
            addView(scroll)
        }
        setContentView(root)

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        handleCi(intent)
        checkUpdate()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleCi(intent)
    }

    /** Commandes utilisees par les tests automatiques (emulateur). */
    private fun handleCi(i: Intent?) {
        when (i?.getStringExtra("ci")) {
            "start" -> startIsland()
            "test" -> sendTest()
            "notch" -> setNotch(true)
            "island" -> setNotch(false)
        }
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    // ---------- Mise a jour ----------

    private fun buildUpdateCard(): View {
        updateCard = card(strong = true).apply { visibility = View.GONE }
        updateText = text("", 15f, Glass.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) }
        updateCard.addView(updateText)
        updateBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progressTintList = ColorStateList.valueOf(Glass.ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(Color.argb(40, 24, 27, 42))
            visibility = View.GONE
        }
        updateCard.addView(updateBar, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(10)).apply { topMargin = dp(10) })
        updateButton = glassButton("Autoriser et mettre à jour", primary = true) { openInstallPermission() }.apply { visibility = View.GONE }
        updateCard.addView(updateButton, buttonLp(top = 12))
        return FrameLayout(this).apply {
            setPadding(0, 0, 0, dp(14))
            addView(updateCard)
        }
    }

    private fun checkUpdate() {
        Updater.check(this) { s ->
            when (s) {
                is Updater.State.UpToDate -> updateCard.visibility = View.GONE
                is Updater.State.Available -> showUpdate("Nouvelle version disponible…", bar = false, button = false)
                is Updater.State.Downloading -> {
                    showUpdate("Téléchargement de la mise à jour… ${s.percent} %", bar = true, button = false)
                    updateBar.progress = s.percent
                }
                is Updater.State.NeedsPermission ->
                    showUpdate("Nouvelle version disponible. Autorise Wave Island à installer ses mises à jour.", bar = false, button = true)
                is Updater.State.Installing -> showUpdate("Installation de la mise à jour…", bar = false, button = false)
                is Updater.State.Error -> updateCard.visibility = View.GONE
            }
        }
    }

    private fun showUpdate(msg: String, bar: Boolean, button: Boolean) {
        updateCard.visibility = View.VISIBLE
        updateText.text = msg
        updateBar.visibility = if (bar) View.VISIBLE else View.GONE
        updateButton.visibility = if (button) View.VISIBLE else View.GONE
    }

    private var waitingInstallPermission = false

    private fun openInstallPermission() {
        waitingInstallPermission = true
        open(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
    }

    // ---------- Etat ----------

    private fun refresh() {
        for (r in rows) {
            when (r.check()) {
                true -> setChip(r.chip, "OK", Glass.OK)
                false -> setChip(r.chip, "À faire", Glass.WARN)
                null -> setChip(r.chip, "Ouvrir", Glass.ACCENT)
            }
        }
        val running = IslandService.instance != null
        heroStatus.text = if (running) "Affiché en permanence" else "Arrêté"
        heroButton.text = if (running) "Arrêter" else "Démarrer"
        if (waitingInstallPermission && packageManager.canRequestPackageInstalls()) {
            waitingInstallPermission = false
            checkUpdate()
        }
    }

    private fun listenerEnabled(): Boolean =
        Settings.Secure.getString(contentResolver, "enabled_notification_listeners")
            ?.contains(packageName) == true

    // ---------- Actions ----------

    private fun startIsland(): Boolean {
        if (!Settings.canDrawOverlays(this)) {
            Toast.makeText(this, "Autorise d'abord « Afficher par-dessus les applis »", Toast.LENGTH_LONG).show()
            return false
        }
        Prefs.setEnabled(this, true)
        startForegroundService(Intent(this, IslandService::class.java))
        ui.postDelayed({ refresh() }, 300)
        return true
    }

    private fun toggleIsland() {
        if (IslandService.instance != null) {
            Prefs.setEnabled(this, false)
            stopService(Intent(this, IslandService::class.java))
            ui.postDelayed({ refresh() }, 300)
        } else {
            startIsland()
        }
    }

    private fun sendTest() {
        if (IslandService.instance == null && !startIsland()) return
        IslandService.deliver(
            this,
            IslandNote("Wave Island", "Ceci est une notification de test.", applicationInfo.loadIcon(packageManager), null),
            packageName
        )
    }

    private fun setNotch(v: Boolean) {
        Prefs.setNotch(this, v)
        segIsland.background = if (!v) Glass.tinted(d, dpf(18)) else null
        segIsland.setTextColor(if (!v) Color.WHITE else Glass.INK)
        segNotch.background = if (v) Glass.tinted(d, dpf(18)) else null
        segNotch.setTextColor(if (v) Color.WHITE else Glass.INK)
        offsetRow.alpha = if (v) 0.4f else 1f
        preview.notch = v
        preview.update()
        IslandService.instance?.applySettings()
    }

    private fun openListenerSettings() {
        if (Build.VERSION.SDK_INT >= 30) {
            val i = Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS)
                .putExtra(
                    Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                    ComponentName(this, NotifListener::class.java).flattenToString()
                )
            if (tryOpen(i)) return
        }
        open(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
    }

    private fun openAppInfo() =
        open(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))

    private fun openNotificationSettings() {
        if (!tryOpen(Intent("android.settings.NOTIFICATION_SETTINGS"))) open(Intent(Settings.ACTION_SETTINGS))
    }

    private fun tryOpen(i: Intent): Boolean = try {
        startActivity(i); true
    } catch (e: Exception) {
        false
    }

    private fun open(i: Intent) {
        if (!tryOpen(i)) Toast.makeText(this, "Impossible d'ouvrir ce réglage", Toast.LENGTH_SHORT).show()
    }

    // ---------- Construction de l'interface ----------

    @Suppress("DEPRECATION")
    private fun setupWindow() {
        window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS)
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT
        window.decorView.systemUiVisibility =
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or
                View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
    }

    private fun appearanceCard(): View {
        val c = card()

        // Choix ile / encoche
        val seg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Glass.chip(Glass.INK, dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        fun segItem(label: String, notch: Boolean) = text(label, 15f, Glass.INK).apply {
            gravity = Gravity.CENTER
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            isClickable = true
            setOnClickListener { setNotch(notch) }
        }
        segIsland = segItem("Île", false)
        segNotch = segItem("Encoche", true)
        seg.addView(segIsland, LinearLayout.LayoutParams(0, dp(38), 1f))
        seg.addView(segNotch, LinearLayout.LayoutParams(0, dp(38), 1f))
        c.addView(seg)

        // Apercu
        preview = IslandPreview(this).apply {
            widthDp = Prefs.width(this@MainActivity)
            heightDp = Prefs.height(this@MainActivity)
            offsetDp = Prefs.offset(this@MainActivity)
            notch = Prefs.notch(this@MainActivity)
            setOnClickListener { expanded = !expanded; update() }
        }
        c.addView(preview, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(130)).apply { topMargin = dp(14) })
        c.addView(text("Touche l'aperçu pour voir l'îlot ouvert", 12f, Glass.INK_SOFT).apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(6), 0, dp(4))
        })

        c.addView(slider("Largeur", Prefs.W_MIN, Prefs.W_MAX, Prefs.width(this)) {
            Prefs.setWidth(this, it); preview.widthDp = it; preview.update()
        })
        c.addView(slider("Hauteur", Prefs.H_MIN, Prefs.H_MAX, Prefs.height(this)) {
            Prefs.setHeight(this, it); preview.heightDp = it; preview.update()
        })
        offsetRow = slider("Distance du haut", Prefs.Y_MIN, Prefs.Y_MAX, Prefs.offset(this)) {
            Prefs.setOffset(this, it); preview.offsetDp = it; preview.update()
        }
        c.addView(offsetRow)
        c.addView(glassButton("Tester une notification", primary = true) { sendTest() }, buttonLp(top = 12))

        setNotch(Prefs.notch(this))
        return c
    }

    /** Curseur : la valeur s'applique a l'ilot en direct (quand on relache). */
    private fun slider(label: String, min: Int, max: Int, value: Int, onChange: (Int) -> Unit): View {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(10), 0, 0)
        }
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        head.addView(text(label, 15f, Glass.INK).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val valueText = text("$value dp", 14f, Glass.ACCENT)
        head.addView(valueText)
        box.addView(head)
        val bar = SeekBar(this).apply {
            this.max = max - min
            progress = value - min
            progressTintList = ColorStateList.valueOf(Glass.ACCENT)
            thumbTintList = ColorStateList.valueOf(Glass.ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(Color.argb(60, 24, 27, 42))
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar, p: Int, fromUser: Boolean) {
                    valueText.text = "${p + min} dp"
                    onChange(p + min)
                }
                override fun onStartTrackingTouch(s: SeekBar) {}
                override fun onStopTrackingTouch(s: SeekBar) {
                    IslandService.instance?.applySettings()
                }
            })
        }
        box.addView(bar)
        return box
    }

    private fun text(s: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
    }

    private fun section(title: String) = text(title, 13f, Glass.INK_SOFT).apply {
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        letterSpacing = 0.04f
        setPadding(dp(8), dp(26), 0, dp(8))
    }

    private fun card(strong: Boolean = false) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = Glass.card(d, dpf(26), strong)
        setPadding(dp(18), dp(16), dp(18), dp(16))
    }

    private fun divider() = View(this).apply {
        setBackgroundColor(Color.argb(22, 24, 27, 42))
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, maxOf(1, dp(1) / 2)).apply {
            topMargin = dp(4); bottomMargin = dp(4)
        }
    }

    private fun buttonLp(top: Int = 0, weight: Boolean = false, start: Int = 0) =
        LinearLayout.LayoutParams(
            if (weight) 0 else ViewGroup.LayoutParams.MATCH_PARENT,
            dp(48),
            if (weight) 1f else 0f
        ).apply { topMargin = dp(top); marginStart = dp(start) }

    private fun glassButton(label: String, primary: Boolean, onWhite: Boolean = false, action: () -> Unit) =
        TextView(this).apply {
            text = label
            textSize = 15f
            gravity = Gravity.CENTER
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            val r = dpf(24)
            when {
                primary -> {
                    setTextColor(Color.WHITE)
                    background = Glass.pressable(Glass.tinted(d, r), r, dark = false)
                }
                onWhite -> {
                    setTextColor(Glass.ACCENT)
                    background = Glass.pressable(Glass.card(d, r, strong = true), r)
                }
                else -> {
                    setTextColor(Glass.INK)
                    background = Glass.pressable(Glass.card(d, r, strong = true), r)
                }
            }
            isClickable = true
            setOnClickListener { action() }
        }

    private fun heroCard(): View {
        val c = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = Glass.tinted(d, dpf(30), alpha = 190)
            setPadding(dp(20), dp(20), dp(20), dp(20))
        }
        c.addView(text("L'îlot", 22f, Color.WHITE, bold = true))
        heroStatus = text("", 15f, Color.argb(225, 255, 255, 255))
        c.addView(heroStatus)

        val buttons = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(18), 0, 0)
        }
        heroButton = glassButton("Démarrer", primary = false, onWhite = true) { toggleIsland() }
        buttons.addView(heroButton, buttonLp(weight = true))
        buttons.addView(
            glassButton("Tester", primary = false, onWhite = true) { sendTest() },
            buttonLp(weight = true, start = 10)
        )
        c.addView(buttons)
        return c
    }

    private fun setChip(chip: TextView, label: String, color: Int) {
        chip.text = label
        chip.setTextColor(color)
        chip.background = Glass.chip(color, dpf(12))
    }

    private fun permRow(num: String, title: String, sub: String, check: () -> Boolean?, action: () -> Unit): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(4), dp(10), dp(4), dp(10))
            background = Glass.pressable(ColorDrawable(Color.TRANSPARENT), dpf(16))
            isClickable = true
            setOnClickListener { action() }
        }
        if (num.isNotEmpty()) {
            row.addView(text(num, 14f, Glass.ACCENT, bold = true).apply {
                gravity = Gravity.CENTER
                background = Glass.chip(Glass.ACCENT, dpf(15))
                layoutParams = LinearLayout.LayoutParams(dp(30), dp(30)).apply { marginEnd = dp(14) }
            })
        }
        val texts = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            addView(text(title, 15f, Glass.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) })
            addView(text(sub, 13f, Glass.INK_SOFT))
        }
        row.addView(texts)
        val chip = text("", 12f, Glass.ACCENT).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setPadding(dp(10), dp(4), dp(10), dp(4))
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { marginStart = dp(10) }
        }
        row.addView(chip)
        rows.add(PermRow(chip, check))
        return row
    }

    private fun switchRow(title: String, sub: TextView, initial: Boolean, onChange: (Boolean) -> Unit): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(4), dp(10), dp(4), dp(10))
        }
        val texts = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            addView(text(title, 15f, Glass.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) })
            addView(sub)
        }
        row.addView(texts)
        val states = arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf())
        val sw = Switch(this).apply {
            isChecked = initial
            thumbTintList = ColorStateList(states, intArrayOf(Color.WHITE, Color.WHITE))
            trackTintList = ColorStateList(states, intArrayOf(Glass.ACCENT, Color.argb(70, 24, 27, 42)))
            setOnCheckedChangeListener { _, v -> onChange(v) }
        }
        row.addView(sw)
        row.setOnClickListener { sw.toggle() }
        return row
    }
}
