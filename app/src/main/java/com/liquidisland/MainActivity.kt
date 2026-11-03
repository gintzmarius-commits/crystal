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
import android.graphics.drawable.GradientDrawable
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
    private lateinit var accessWarn: LinearLayout
    private lateinit var accessTitle: TextView
    private lateinit var accessBody: TextView
    private lateinit var preview: IslandPreview
    private lateinit var segIsland: TextView
    private lateinit var segNotch: TextView
    private lateinit var offsetRow: View
    private lateinit var updateCard: LinearLayout
    private lateinit var updateText: TextView
    private lateinit var updateBar: ProgressBar
    private lateinit var updateButton: TextView
    private lateinit var checkStatus: TextView
    private val rows = mutableListOf<PermRow>()

    private class PermRow(val chip: TextView, val check: () -> Boolean?)

    private val isXiaomi = Build.MANUFACTURER.lowercase().let { it.contains("xiaomi") || it.contains("redmi") || it.contains("poco") }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setupWindow()

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(70), dp(18), dp(130))
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

        // Dernier plantage : proposer d'envoyer le rapport
        crashCard = card(strong = true).apply { visibility = View.GONE }
        crashCard.addView(text("Wave Island a planté", 15f, Glass.INK, bold = true))
        crashCard.addView(text("Envoie-moi le rapport (dans la conversation) pour que je corrige la cause.", 13f, Glass.INK_SOFT).apply {
            setPadding(0, dp(4), 0, 0)
        })
        crashCard.addView(glassButton("Envoyer le rapport", primary = true) { shareReport(clearAfter = true) }, buttonLp(top = 10))
        col.addView(FrameLayout(this).apply { setPadding(0, 0, 0, dp(14)); addView(crashCard) })
        col.addView(buildUpdateCard())
        col.addView(heroCard())

        // Apparence
        col.addView(section("Apparence"))
        col.addView(appearanceCard())

        // Indicateur de volume
        col.addView(section("Volume"))
        col.addView(volumeCard())

        // Activites en arriere-plan
        col.addView(section("Activités en cours"))
        val live = card()
        live.addView(switchRow(
            "Musique et vidéos",
            text("Pochette et barres qui dansent sur l'îlot. Touche l'îlot : morceau précédent, pause, suivant.", 13f, Glass.INK_SOFT),
            Prefs.liveMedia(this)
        ) { Prefs.setLiveMedia(this, it); IslandService.instance?.applySettings() })
        live.addView(switchRow(
            "Téléchargements et tâches",
            text("Une barre de progression par téléchargement (navigateur, Play Store, envois...).", 13f, Glass.INK_SOFT),
            Prefs.liveDownloads(this)
        ) { Prefs.setLiveDownloads(this, it); IslandService.instance?.applySettings() })
        col.addView(live)

        // Raccourcis du mini menu
        col.addView(section("Menu de l'îlot"))
        col.addView(shortcutsCard())

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
        perms.addView(permRow("3", "Accessibilité", "Pour toucher l'îlot par-dessus la barre d'état", {
            accessibilityEnabled()
        }) { open(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) })
        perms.addView(divider())
        perms.addView(permRow("✦", "Effet goutte d'eau en direct", "Capture d'écran : l'effet suit l'écran sans délai", {
            if (Build.VERSION.SDK_INT < 33) null else IslandService.instance?.isLive == true
        }) { askLive() })
        perms.addView(divider())
        perms.addView(permRow("4", "Batterie sans restriction", "Pour que l'îlot ne soit jamais coupé", {
            (getSystemService(Context.POWER_SERVICE) as PowerManager).isIgnoringBatteryOptimizations(packageName)
        }) { open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))) })
        perms.addView(divider())
        perms.addView(permRow("5", "Mises à jour automatiques", "Autoriser l'installation des nouvelles versions", {
            packageManager.canRequestPackageInstalls()
        }) { openInstallPermission() })
        if (isXiaomi) {
            perms.addView(divider())
            perms.addView(permRow("6", "Démarrage automatique", "Infos de l'appli › Démarrage auto", { null }) { openAppInfo() })
        }
        col.addView(perms)

        // Aide paramètres restreints
        col.addView(section("Réglage grisé ou « paramètre restreint » ?"))
        val help = card()
        help.addView(text(
            "Android bloque l'accès aux notifications et l'accessibilité pour les applis installées hors Play Store. Pour les débloquer :\n\n" +
                "1. Touche « Accès aux notifications » ou « Accessibilité » ci-dessus et essaie d'activer Wave Island (un message « paramètre restreint » s'affiche).\n" +
                "2. Ouvre les infos de l'appli avec le bouton ci-dessous.\n" +
                "3. Touche le menu ⋮ en haut à droite, puis « Autoriser les paramètres restreints ».\n" +
                "4. Reviens ici et active l'accès aux notifications et l'accessibilité.",
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

        // Version et verification manuelle des mises a jour
        col.addView(section("Mises à jour"))
        val upd = card()
        upd.addView(text("Version installée : ${BuildConfig.VERSION_NAME}", 15f, Glass.INK).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        })
        checkStatus = text("", 13f, Glass.INK_SOFT).apply { setPadding(0, dp(4), 0, 0) }
        upd.addView(checkStatus)
        upd.addView(glassButton("Vérifier les mises à jour", primary = false) { checkUpdate(manual = true) }, buttonLp(top = 12))
        upd.addView(glassButton("Envoyer un rapport de bug", primary = false) { shareReport(clearAfter = false) }, buttonLp(top = 10))
        col.addView(upd)

        val scroll = ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            addView(col)
        }
        settingsPage = scroll
        dropPage = buildDropPage().apply { visibility = View.GONE }
        val root = FrameLayout(this).apply {
            background = Glass.background(resources.displayMetrics.widthPixels)
            addView(scroll)
            addView(dropPage)
            // Onglets en bas : Parametres et Wave Drop
            addView(tabBar(), FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(58), Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply {
                bottomMargin = dp(24)
            })
        }
        setContentView(root)

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        handleCi(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleCi(intent)
    }

    /** Commandes utilisees par les tests automatiques (emulateur). */
    private fun handleCi(i: Intent?) {
        when (i?.getStringExtra("ci")) {
            "start" -> startIsland()
            "drop" -> selectTab(1)
            "dropon" -> { selectTab(1); enableDrop(true) }
            "dropsend" -> com.liquidisland.drop.WaveDropActivity.open(this)
            "test" -> sendTest()
            "notch" -> setNotch(true)
            "island" -> setNotch(false)
            "menu" -> IslandService.instance?.showMenu()
            "live" -> askLive()
            "stop" -> { Prefs.setEnabled(this, false); stopService(Intent(this, IslandService::class.java)) }
            "fullscreen" -> if (Build.VERSION.SDK_INT >= 30) window.insetsController?.hide(android.view.WindowInsets.Type.statusBars())
            "exitfullscreen" -> if (Build.VERSION.SDK_INT >= 30) window.insetsController?.show(android.view.WindowInsets.Type.statusBars())
            "testbg" -> {
                // Notif de test puis retour a l'accueil : sert a verifier que toucher la notif rouvre l'appli
                sendTest()
                ui.postDelayed({ moveTaskToBack(true) }, 150)
            }
        }
    }

    // ---------- Onglets ----------

    private lateinit var settingsPage: View
    private lateinit var dropPage: View
    private val tabs = mutableListOf<TextView>()
    private var tab = 0

    private fun tabBar(): View {
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = GradientDrawable().apply {
                cornerRadius = dpf(29)
                setColor(Color.argb(200, 16, 26, 22))
                setStroke(maxOf(1, dp(1)), Color.argb(70, 255, 255, 255))
            }
            setPadding(dp(5), dp(5), dp(5), dp(5))
            elevation = dpf(10)
        }
        listOf("⚙  Paramètres", "📡  Wave Drop").forEachIndexed { i, label ->
            val t = text(label, 15f, Glass.INK).apply {
                gravity = Gravity.CENTER
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                setPadding(dp(20), 0, dp(20), 0)
                isClickable = true
                setOnClickListener { selectTab(i) }
            }
            tabs.add(t)
            bar.addView(t, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT))
        }
        paintTabs()
        return bar
    }

    private fun paintTabs() {
        tabs.forEachIndexed { i, t ->
            val on = i == tab
            t.background = if (on) Glass.tinted(d, dpf(24)) else null
            t.setTextColor(if (on) Glass.ON_ACCENT else Glass.INK)
        }
    }

    private fun selectTab(i: Int) {
        if (i == tab) return
        tab = i
        paintTabs()
        val show = if (i == 0) settingsPage else dropPage
        val hide = if (i == 0) dropPage else settingsPage
        hide.animate().alpha(0f).setDuration(120).withEndAction { hide.visibility = View.GONE }.start()
        show.alpha = 0f
        show.translationY = dpf(12)
        show.visibility = View.VISIBLE
        val s = Spring.SOFT
        show.animate().alpha(1f).translationY(0f).setDuration(s.durationMs).setInterpolator(s).start()
        if (i == 1) { refreshDrop(); ui.removeCallbacks(dropTick); ui.post(dropTick) } else ui.removeCallbacks(dropTick)
    }

    // ---------- Wave Drop ----------

    private lateinit var dropSwitch: Switch
    private lateinit var dropStatus: TextView
    private lateinit var dropNearby: LinearLayout
    private lateinit var dropHistory: LinearLayout
    private val dropTick = object : Runnable {
        override fun run() { refreshDropNearby(); ui.postDelayed(this, 2000) }
    }

    private fun buildDropPage(): View {
        com.liquidisland.drop.WaveDrop.init(this)
        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(70), dp(18), dp(130))
        }
        col.addView(text("Wave Drop", 32f, Glass.INK, bold = true))
        col.addView(text("Envoie n'importe quel fichier à un appareil proche, comme AirDrop : téléphone ou PC avec Wave Island.", 15f, Glass.INK_SOFT).apply {
            setPadding(0, dp(2), 0, dp(18))
        })

        val main = card(strong = true)
        val row = switchRow(
            "Activer Wave Drop",
            text("Ton appareil devient visible des appareils Wave Drop tout proches", 13f, Glass.INK_SOFT),
            com.liquidisland.drop.WaveDrop.enabled(this)
        ) { on -> if (on && !com.liquidisland.drop.WaveDrop.hasPermissions(this)) {
                requestPermissions(com.liquidisland.drop.WaveDrop.permissions(), DROP_PERMS)
            } else enableDrop(on)
        }
        dropSwitch = (row as LinearLayout).getChildAt(1) as Switch
        main.addView(row)
        main.addView(divider())
        main.addView(text("Nom de cet appareil", 13f, Glass.INK_SOFT).apply { setPadding(dp(4), dp(8), 0, dp(4)) })
        val name = android.widget.EditText(this).apply {
            setText(com.liquidisland.drop.WaveDrop.myName)
            setTextColor(Glass.INK)
            textSize = 16f
            setSingleLine()
            background = Glass.card(d, dpf(16), strong = true)
            setPadding(dp(14), dp(10), dp(14), dp(10))
            addTextChangedListener(object : android.text.TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(e: android.text.Editable?) {
                    if (!e.isNullOrBlank()) com.liquidisland.drop.WaveDrop.myName = e.toString()
                }
            })
        }
        main.addView(name)
        dropStatus = text("", 13f, Glass.INK_SOFT).apply { setPadding(dp(4), dp(12), dp(4), 0) }
        main.addView(dropStatus)
        main.addView(glassButton("Envoyer un fichier", primary = true) { com.liquidisland.drop.WaveDropActivity.open(this) }, buttonLp(top = 14))
        main.addView(text("Astuce : glisse l'îlot vers le bas pour ouvrir Wave Drop depuis n'importe où.", 12f, Glass.INK_SOFT).apply {
            setPadding(dp(4), dp(10), dp(4), 0)
        })
        col.addView(main)

        col.addView(section("Appareils autour"))
        dropNearby = card()
        col.addView(dropNearby)

        col.addView(section("Reçus récemment"))
        dropHistory = card()
        col.addView(dropHistory)

        col.addView(section("Comment ça marche"))
        val how = card()
        how.addView(text(
            "• Les deux appareils ont Wave Island et Wave Drop activé.\n" +
                "• Choisis le fichier (glisse l'îlot vers le bas), puis colle l'autre téléphone au dos du tien (NFC) " +
                "ou rapproche l'appareil (Bluetooth) : il apparaît, touche « Envoyer ».\n" +
                "• L'autre doit accepter avant que quoi que ce soit n'arrive. Tout est chiffré de bout en bout.\n" +
                "• Même Wi-Fi : transfert rapide. Sinon : Bluetooth, automatiquement (plus lent).\n" +
                "• Les fichiers reçus vont dans Téléchargements › Wave Drop.",
            14f, Glass.INK_SOFT
        ).apply { setLineSpacing(0f, 1.2f) })
        col.addView(how)

        return ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            addView(col)
        }
    }

    private fun enableDrop(on: Boolean) {
        val wd = com.liquidisland.drop.WaveDrop
        wd.setEnabled(this, on)
        if (on) { wd.restart(this); if (IslandService.instance == null) startIsland() } else wd.stop()
        if (::dropSwitch.isInitialized && dropSwitch.isChecked != on) dropSwitch.isChecked = on
        refreshDrop()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != DROP_PERMS) return
        if (grantResults.isNotEmpty() && grantResults.all { it == PackageManager.PERMISSION_GRANTED }) enableDrop(true)
        else {
            Toast.makeText(this, "Bluetooth refusé : Wave Drop marchera seulement sur le même Wi-Fi", Toast.LENGTH_LONG).show()
            enableDrop(true)
        }
    }

    private fun refreshDrop() {
        if (!::dropStatus.isInitialized) return
        val wd = com.liquidisland.drop.WaveDrop
        val bt = (getSystemService(Context.BLUETOOTH_SERVICE) as? android.bluetooth.BluetoothManager)?.adapter
        val nfc = try { android.nfc.NfcAdapter.getDefaultAdapter(this) } catch (e: Exception) { null }
        val wifi = (getSystemService(Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager).let { cm ->
            cm.getNetworkCapabilities(cm.activeNetwork)?.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI) == true
        }
        dropStatus.text = listOf(
            "Bluetooth : " + when { bt == null -> "absent"; !bt.isEnabled -> "désactivé"; !wd.hasPermissions(this) -> "non autorisé"; else -> "prêt" },
            "NFC : " + when { nfc == null -> "absent"; !nfc.isEnabled -> "désactivé"; else -> "prêt" },
            "Wi-Fi : " + if (wifi) "connecté" else "non connecté"
        ).joinToString("   ·   ") + if (wd.enabled(this)) "" else "\nWave Drop est désactivé."
        refreshDropNearby()
        dropHistory.removeAllViews()
        val items = com.liquidisland.drop.History.list(this)
        if (items.isEmpty()) dropHistory.addView(text("Rien reçu pour l'instant.", 14f, Glass.INK_SOFT))
        items.take(10).forEach { h ->
            val r = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(4), dp(8), dp(4), dp(8))
                background = Glass.pressable(ColorDrawable(Color.TRANSPARENT), dpf(14))
                isClickable = true
                setOnClickListener { h.uri?.let { com.liquidisland.drop.WaveDropActivity.openFile(this@MainActivity, Uri.parse(it), null) } }
            }
            r.addView(text(h.name, 15f, Glass.INK).apply { maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.MIDDLE })
            r.addView(text("De ${h.from} · " + android.text.format.DateUtils.getRelativeTimeSpanString(h.time), 12f, Glass.INK_SOFT))
            dropHistory.addView(r)
        }
    }

    private fun refreshDropNearby() {
        if (!::dropNearby.isInitialized) return
        dropNearby.removeAllViews()
        val wd = com.liquidisland.drop.WaveDrop
        val list = if (wd.running) wd.nearbyList() else emptyList()
        if (list.isEmpty()) dropNearby.addView(text(if (wd.running) "Aucun appareil Wave Drop autour." else "Active Wave Drop pour voir les appareils autour.", 14f, Glass.INK_SOFT))
        list.take(6).forEach { n ->
            dropNearby.addView(text((if (n.beacon.isPc) "💻  " else "📱  ") + n.label + "   ·   " +
                (if (n.close) "tout près" else "à proximité") + (if (n.beacon.ip != null) " · Wi-Fi" else " · Bluetooth"), 14f, Glass.INK).apply {
                setPadding(dp(4), dp(6), dp(4), dp(6))
            })
        }
    }

    // ---------- Volume ----------

    private fun volumeCard(): View {
        val c = card()
        c.addView(switchRow(
            "Indicateur de volume façon iPhone",
            text("Une capsule en verre à côté des boutons, à la place de celle du téléphone. Touche-la pour régler au doigt.", 13f, Glass.INK_SOFT),
            Prefs.volumeHud(this)
        ) {
            Prefs.setVolumeHud(this, it)
            if (it) IslandService.instance?.previewVolume()
        })
        c.addView(text("Côté des boutons de volume", 14f, Glass.INK_SOFT).apply { setPadding(dp(4), dp(12), 0, dp(6)) })
        val seg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Glass.chip(Glass.INK, dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val sides = listOf("left" to "Gauche", "right" to "Droite")
        val views = mutableListOf<TextView>()
        fun paint() {
            val cur = Prefs.volumeSide(this)
            sides.forEachIndexed { i, (key, _) ->
                val on = key == cur
                views[i].background = if (on) Glass.tinted(d, dpf(18)) else null
                views[i].setTextColor(if (on) Glass.ON_ACCENT else Glass.INK)
            }
        }
        sides.forEach { (key, label) ->
            val t = text(label, 14f, Glass.INK).apply {
                gravity = Gravity.CENTER
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isClickable = true
                setOnClickListener {
                    Prefs.setVolumeSide(this@MainActivity, key)
                    paint()
                    IslandService.instance?.previewVolume()
                }
            }
            views.add(t)
            seg.addView(t, LinearLayout.LayoutParams(0, dp(36), 1f))
        }
        c.addView(seg)
        paint()
        val pos = slider("Hauteur (à hauteur des boutons)", 10, 75, Prefs.volumePos(this)) {
            Prefs.setVolumePos(this, it)
            IslandService.instance?.previewVolume()
        }
        // Le curseur affiche des « dp » : ici ce sont des % de l'ecran
        c.addView(pos)
        c.addView(text("Appuie sur un bouton de volume pour voir l'indicateur. Il demande l'accessibilité.", 12f, Glass.INK_SOFT).apply {
            setPadding(dp(4), dp(8), 0, 0)
        })
        return c
    }

    // ---------- Raccourcis ----------

    private lateinit var shortcutsList: LinearLayout

    private fun shortcutsCard(): View {
        val c = card()
        c.addView(text(
            "Reste appuyé sur l'îlot pour ouvrir ce menu. Jusqu'à ${Shortcuts.MAX} raccourcis.",
            14f, Glass.INK_SOFT
        ).apply { setPadding(0, 0, 0, dp(6)) })
        shortcutsList = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        c.addView(shortcutsList)
        c.addView(glassButton("Ajouter un raccourci", primary = false) { addShortcutDialog() }, buttonLp(top = 12))
        renderShortcuts()
        return c
    }

    private fun renderShortcuts() {
        shortcutsList.removeAllViews()
        val ids = Shortcuts.load(this)
        ids.forEachIndexed { i, id ->
            val item = Shortcuts.resolve(this, id) ?: return@forEachIndexed
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(2), dp(8), 0, dp(8))
            }
            row.addView(android.widget.ImageView(this).apply {
                setImageDrawable(item.icon)
                if (item.tintIcon) imageTintList = ColorStateList.valueOf(Glass.INK)
                val p = if (item.tintIcon) dp(6) else 0
                setPadding(p, p, p, p)
                background = Glass.chip(Glass.ACCENT, dpf(18))
            }, LinearLayout.LayoutParams(dp(36), dp(36)).apply { marginEnd = dp(14) })
            row.addView(text(item.label, 15f, Glass.INK), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            if (i > 0) row.addView(smallButton("↑") {
                val l = ids.toMutableList(); l.removeAt(i); l.add(i - 1, id); saveShortcuts(l)
            })
            row.addView(smallButton("✕") {
                saveShortcuts(ids.filterIndexed { j, _ -> j != i })
            })
            shortcutsList.addView(row)
            if (i < ids.size - 1) shortcutsList.addView(divider())
        }
        if (ids.isEmpty()) shortcutsList.addView(text("Aucun raccourci pour l'instant.", 14f, Glass.INK_SOFT))
    }

    private fun smallButton(label: String, action: () -> Unit) = text(label, 16f, Glass.INK_SOFT).apply {
        gravity = Gravity.CENTER
        background = Glass.pressable(ColorDrawable(Color.TRANSPARENT), dpf(18))
        isClickable = true
        setOnClickListener { action() }
        layoutParams = LinearLayout.LayoutParams(dp(40), dp(40))
    }

    private fun saveShortcuts(list: List<String>) {
        Shortcuts.save(this, list)
        renderShortcuts()
        IslandService.instance?.applySettings()
    }

    private fun addShortcutDialog() {
        val current = Shortcuts.load(this)
        if (current.size >= Shortcuts.MAX) {
            Toast.makeText(this, "Maximum ${Shortcuts.MAX} raccourcis : retires-en un d'abord", Toast.LENGTH_SHORT).show()
            return
        }
        val choices = Shortcuts.ACTIONS.filter { it.first !in current }.map { it.first to "⚡ " + it.second } +
            Shortcuts.launchableApps(this).filter { it.first !in current }
        android.app.AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("Ajouter un raccourci")
            .setItems(choices.map { it.second }.toTypedArray()) { _, w -> saveShortcuts(current + choices[w].first) }
            .setNegativeButton("Annuler", null)
            .show()
    }

    override fun onResume() {
        super.onResume()
        refresh()
        refreshDrop()
        // Une seule fois par lancement : proposer l'effet goutte en direct s'il n'est pas actif
        if (!liveAsked && Build.VERSION.SDK_INT >= 33 && Prefs.refraction(this) &&
            IslandService.instance != null && IslandService.instance?.isLive != true
        ) {
            liveAsked = true
            ui.postDelayed({ askLive() }, 600)
        }
        // Verifie les mises a jour a chaque retour dans l'appli (pas plus d'une fois par minute)
        if (System.currentTimeMillis() - lastCheck > 60_000) checkUpdate(manual = false)
    }

    private var lastCheck = 0L

    private lateinit var crashCard: LinearLayout

    /** Partage le rapport (appareil, plantage, journal) : a coller dans la conversation. */
    private fun shareReport(clearAfter: Boolean) {
        Thread {
            val text = WaveApp.report(this)
            ui.post {
                try {
                    startActivity(Intent.createChooser(
                        Intent(Intent.ACTION_SEND).setType("text/plain")
                            .putExtra(Intent.EXTRA_SUBJECT, "Rapport Wave Island")
                            .putExtra(Intent.EXTRA_TEXT, text),
                        "Envoyer le rapport"
                    ))
                    if (clearAfter) { WaveApp.clearCrash(this); refresh() }
                } catch (e: Exception) {
                    Toast.makeText(this, "Partage impossible", Toast.LENGTH_SHORT).show()
                }
            }
        }.start()
    }

    private fun askLive() {
        if (Build.VERSION.SDK_INT < 33) {
            Toast.makeText(this, "L'effet en direct demande Android 13 ou plus", Toast.LENGTH_LONG).show()
            return
        }
        open(Intent(this, CaptureConsentActivity::class.java))
    }

    companion object {
        const val DROP_PERMS = 77
        /** Demande de capture deja proposee pendant ce lancement de l'appli */
        private var liveAsked = false
    }

    // ---------- Mise a jour ----------

    private fun buildUpdateCard(): View {
        updateCard = card(strong = true).apply { visibility = View.GONE }
        updateText = text("", 15f, Glass.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) }
        updateCard.addView(updateText)
        updateBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progressTintList = ColorStateList.valueOf(Glass.ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(Color.argb(40, 255, 255, 255))
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

    private fun checkUpdate(manual: Boolean) {
        lastCheck = System.currentTimeMillis()
        if (manual) checkStatus.text = "Recherche d'une nouvelle version…"
        Updater.check(this) { s ->
            when (s) {
                is Updater.State.UpToDate -> {
                    updateCard.visibility = View.GONE
                    checkStatus.text = "Tu as la dernière version."
                }
                is Updater.State.Available -> showUpdate("Nouvelle version disponible…", bar = false, button = false)
                is Updater.State.Downloading -> {
                    showUpdate("Téléchargement de la mise à jour… ${s.percent} %", bar = true, button = false)
                    updateBar.progress = s.percent
                }
                is Updater.State.NeedsPermission ->
                    showUpdate("Nouvelle version disponible. Autorise Wave Island à installer ses mises à jour.", bar = false, button = true)
                is Updater.State.Installing -> showUpdate("Installation de la mise à jour…", bar = false, button = false)
                is Updater.State.Error -> {
                    updateCard.visibility = View.GONE
                    checkStatus.text = "Impossible de vérifier les mises à jour (${s.message}). Vérifie ta connexion."
                }
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
        // Cochee dans les reglages mais pas connectee = Android l'a arretee (souvent apres un plantage)
        val stopped = accessibilityEnabled() && IslandAccessibility.instance == null
        accessWarn.visibility = if (accessibilityEnabled() && !stopped) View.GONE else View.VISIBLE
        if (stopped) {
            accessTitle.text = "Android a arrêté l'accessibilité de Wave Island"
            accessBody.text = "C'est pour ça que l'îlot passe sous la barre d'état et que l'effet verre disparaît. " +
                "Dans Accessibilité › Wave Island : désactive-la puis réactive-la."
        } else {
            accessTitle.text = "Toucher l'îlot ouvre le volet des réglages ?"
            accessBody.text = "Active « Accessibilité › Wave Island » : l'îlot passe alors au-dessus de la barre d'état et répond à tes doigts. " +
                "Si c'est grisé, suis l'aide « paramètre restreint » plus bas."
        }
        crashCard.visibility = if (WaveApp.crash(this) != null) View.VISIBLE else View.GONE
        if (waitingInstallPermission && packageManager.canRequestPackageInstalls()) {
            waitingInstallPermission = false
            checkUpdate(manual = true)
        }
    }

    private fun accessibilityEnabled(): Boolean =
        Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            ?.contains(packageName) == true

    private fun listenerEnabled(): Boolean =
        Settings.Secure.getString(contentResolver, "enabled_notification_listeners")
            ?.contains(packageName) == true

    // ---------- Actions ----------

    private fun startIsland(): Boolean {
        if (!Settings.canDrawOverlays(this) && IslandAccessibility.instance == null) {
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
        segIsland.setTextColor(if (!v) Glass.ON_ACCENT else Glass.INK)
        segNotch.background = if (v) Glass.tinted(d, dpf(18)) else null
        segNotch.setTextColor(if (v) Glass.ON_ACCENT else Glass.INK)
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
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
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

        // Teinte du verre
        c.addView(text("Teinte du verre", 14f, Glass.INK_SOFT).apply { setPadding(dp(4), dp(14), 0, dp(6)) })
        val themeSeg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Glass.chip(Glass.INK, dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val themes = listOf("clear" to "Transparent", "dark" to "Sombre", "light" to "Claire")
        val themeViews = mutableListOf<TextView>()
        fun paintThemes() {
            val cur = Prefs.theme(this)
            themes.forEachIndexed { i, (key, _) ->
                val on = key == cur
                themeViews[i].background = if (on) Glass.tinted(d, dpf(18)) else null
                themeViews[i].setTextColor(if (on) Glass.ON_ACCENT else Glass.INK)
            }
        }
        themes.forEach { (key, label) ->
            val t = text(label, 14f, Glass.INK).apply {
                gravity = Gravity.CENTER
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isClickable = true
                setOnClickListener {
                    Prefs.setTheme(this@MainActivity, key)
                    paintThemes()
                    preview.update()
                    IslandService.instance?.applySettings()
                }
            }
            themeViews.add(t)
            themeSeg.addView(t, LinearLayout.LayoutParams(0, dp(36), 1f))
        }
        c.addView(themeSeg)
        paintThemes()

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
        c.addView(switchRow(
            "Déformation du fond (effet goutte d'eau)",
            text("Le bord de l'îlot déforme ce qui est derrière lui. Android 13+, avec l'accessibilité", 13f, Glass.INK_SOFT),
            Prefs.refraction(this)
        ) {
            Prefs.setRefraction(this, it)
            IslandService.instance?.applySettings()
        })
        // En plein ecran
        c.addView(text("En plein écran (vidéo, jeu)", 14f, Glass.INK_SOFT).apply { setPadding(dp(4), dp(14), 0, dp(6)) })
        val fsSeg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Glass.chip(Glass.INK, dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val fsModes = listOf("show" to "Contre la caméra", "hide" to "Cacher l'îlot")
        val fsViews = mutableListOf<TextView>()
        fun paintFs() {
            val cur = Prefs.fullscreen(this)
            fsModes.forEachIndexed { i, (key, _) ->
                val on = key == cur
                fsViews[i].background = if (on) Glass.tinted(d, dpf(18)) else null
                fsViews[i].setTextColor(if (on) Glass.ON_ACCENT else Glass.INK)
            }
        }
        fsModes.forEach { (key, label) ->
            val t = text(label, 14f, Glass.INK).apply {
                gravity = Gravity.CENTER
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isClickable = true
                setOnClickListener {
                    Prefs.setFullscreen(this@MainActivity, key)
                    paintFs()
                    IslandService.instance?.applySettings()
                }
            }
            fsViews.add(t)
            fsSeg.addView(t, LinearLayout.LayoutParams(0, dp(36), 1f))
        }
        c.addView(fsSeg)
        paintFs()
        c.addView(glassButton("Tester une notification", primary = true) { sendTest() }, buttonLp(top = 12))

        setNotch(Prefs.notch(this))
        return c
    }

    /** Curseur : la valeur s'applique a l'ilot en direct (quand on relache). */
    private fun slider(label: String, min: Int, max: Int, value: Int, onChange: (Int) -> Unit): View =
        slider(label, min, max, value, if (label.startsWith("Hauteur (")) "%" else "dp", onChange)

    private fun slider(label: String, min: Int, max: Int, value: Int, unit: String, onChange: (Int) -> Unit): View {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(10), 0, 0)
        }
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        head.addView(text(label, 15f, Glass.INK).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val valueText = text("$value $unit", 14f, Glass.ACCENT)
        head.addView(valueText)
        box.addView(head)
        val bar = SeekBar(this).apply {
            this.max = max - min
            progress = value - min
            progressTintList = ColorStateList.valueOf(Glass.ACCENT)
            thumbTintList = ColorStateList.valueOf(Glass.ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(Color.argb(50, 255, 255, 255))
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar, p: Int, fromUser: Boolean) {
                    valueText.text = "${p + min} $unit"
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
        setBackgroundColor(Color.argb(24, 255, 255, 255))
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
                    setTextColor(Glass.ON_ACCENT)
                    background = Glass.pressable(Glass.tinted(d, r), r, dark = true)
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
            background = Glass.card(d, dpf(30), strong = true)
            setPadding(dp(20), dp(20), dp(20), dp(20))
        }
        c.addView(text("L'îlot", 22f, Color.WHITE, bold = true))
        heroStatus = text("", 15f, Color.argb(225, 255, 255, 255))
        c.addView(heroStatus)

        // Sans l'accessibilite, la barre d'etat recoit les touchers a la place de l'ilot
        accessWarn = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = Glass.card(d, dpf(20), strong = true)
            setPadding(dp(14), dp(12), dp(14), dp(12))
            visibility = View.GONE
        }
        accessTitle = text("Toucher l'îlot ouvre le volet des réglages ?", 15f, Glass.INK, bold = true)
        accessWarn.addView(accessTitle)
        accessBody = text("", 13f, Glass.INK_SOFT).apply { setPadding(0, dp(4), 0, 0) }
        accessWarn.addView(accessBody)
        accessWarn.addView(glassButton("Activer l'accessibilité", primary = true) {
            open(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }, buttonLp(top = 10))
        c.addView(accessWarn, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(14)
        })

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
            trackTintList = ColorStateList(states, intArrayOf(Glass.ACCENT, Color.argb(70, 255, 255, 255)))
            setOnCheckedChangeListener { _, v -> onChange(v) }
        }
        row.addView(sw)
        row.setOnClickListener { sw.toggle() }
        return row
    }
}
