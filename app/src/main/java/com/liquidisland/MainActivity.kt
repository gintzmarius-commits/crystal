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
        header.addView(text("Wave Island", 32f, Look.INK, bold = true))
        col.addView(header)
        col.addView(text("Ton îlot dynamique en verre, toujours à l'écran.", 15f, Look.INK_SOFT).apply {
            setPadding(0, dp(2), 0, dp(18))
        })

        // Dernier plantage : proposer d'envoyer le rapport
        crashCard = card(strong = true).apply { visibility = View.GONE }
        crashCard.addView(text("Wave Island a planté", 15f, Look.INK, bold = true))
        crashCard.addView(text("Envoie-moi le rapport (dans la conversation) pour que je corrige la cause.", 13f, Look.INK_SOFT).apply {
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
            text("Pochette et barres qui dansent sur l'îlot. Touche l'îlot : morceau précédent, pause, suivant.", 13f, Look.INK_SOFT),
            Prefs.liveMedia(this)
        ) { Prefs.setLiveMedia(this, it); IslandService.instance?.applySettings() })
        live.addView(switchRow(
            "Téléchargements et tâches",
            text("Une barre de progression par téléchargement (navigateur, Play Store, envois...).", 13f, Look.INK_SOFT),
            Prefs.liveDownloads(this)
        ) { Prefs.setLiveDownloads(this, it); IslandService.instance?.applySettings() })
        live.addView(switchRow(
            "Dock Wave Theme",
            text("Dans une appli : glisse tout en bas de l'écran (sur la petite barre) pour faire monter le dock. Il reste 3 secondes ; pendant ce temps, glisse encore pour fermer l'appli comme d'habitude (c'est Android qui s'en charge). Appui long sur une icône du dock : l'ajouter ou la retirer des favoris.", 13f, Look.INK_SOFT),
            Prefs.dock(this)
        ) { Prefs.setDock(this, it); IslandService.instance?.updateStrip() })
        live.addView(text("Applis du dock (${WaveDock.MAX} au maximum)", 14f, Look.INK).apply { setPadding(dp(4), dp(10), 0, dp(2)) })
        dockList = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        live.addView(dockList)
        live.addView(glassButton("Ajouter une appli au dock", primary = false) { addDockDialog() }, buttonLp(top = 8))
        renderDock()
        live.addView(switchRow(
            "Centres de notifications et de contrôle",
            text("Tire la barre d'état : à gauche toutes tes notifications, à droite les réglages rapides (Wi-Fi, Bluetooth, lampe, luminosité, volume, musique…), en verre liquide.", 13f, Look.INK_SOFT),
            Prefs.shade(this)
        ) { Prefs.setShade(this, it); IslandService.instance?.updateStrip() })
        col.addView(live)

        // Lot 4 : Wave Link (PC + telephone)
        col.addView(section("Wave Link (téléphone + PC)"))
        col.addView(linkCard())

        // Version finale : assistant IA (Claude)
        col.addView(section("Assistant IA (Gemini)"))
        col.addView(assistCard())

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
                14f, Look.INK_SOFT
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
            14f, Look.INK_SOFT
        ).apply { setLineSpacing(0f, 1.15f) })
        help.addView(glassButton("Ouvrir les infos de l'appli", primary = false) { openAppInfo() }, buttonLp(top = 14))
        col.addView(help)

        // Options
        col.addView(section("Options"))
        val opts = card()
        opts.addView(text("Les notifications s'affichent dans l'îlot et restent aussi dans le volet de notifications du téléphone.", 13f, Look.INK_SOFT).apply { setPadding(0, 0, 0, dp(8)) })
        opts.addView(permRow("", "Notifications pop-up du système", "À couper pour ne garder que l'îlot en haut (elles restent dans le volet)", { null }) {
            openNotificationSettings()
        })
        col.addView(opts)

        // Version et verification manuelle des mises a jour
        col.addView(section("Mises à jour"))
        val upd = card()
        upd.addView(text("Version installée : ${BuildConfig.VERSION_NAME}", 15f, Look.INK).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        })
        checkStatus = text("", 13f, Look.INK_SOFT).apply { setPadding(0, dp(4), 0, 0) }
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
            background = Look.background(resources.displayMetrics.widthPixels)
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
            "shade" -> com.liquidisland.shade.ShadeActivity.open(this, com.liquidisland.shade.ShadeActivity.NOTIFS)
            "controls" -> com.liquidisland.shade.ShadeActivity.open(this, com.liquidisland.shade.ShadeActivity.CONTROLS)
            "test" -> sendTest()
            "notch" -> setNotch(true)
            "island" -> setNotch(false)
            "menu" -> IslandService.instance?.showMenu()
            "assist0" -> com.liquidisland.assist.Assistant.demo(this, 0)
            "assist1" -> com.liquidisland.assist.Assistant.demo(this, 1)
            "assist2" -> com.liquidisland.assist.Assistant.demo(this, 2)
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
                setColor(Color.WHITE)
                setStroke(maxOf(1, dp(1)), Look.LINE)
            }
            setPadding(dp(5), dp(5), dp(5), dp(5))
            elevation = dpf(10)
        }
        listOf("Paramètres" to LineIcon.Kind.SETTINGS, "Wave Drop" to LineIcon.Kind.DROP).forEachIndexed { i, (label, kind) ->
            val t = text(label, 15f, Look.INK).apply {
                gravity = Gravity.CENTER
                tag = kind
                compoundDrawablePadding = dp(8)
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
            t.background = if (on) Look.tinted(d, dpf(24)) else null
            t.setTextColor(if (on) Look.ON_ACCENT else Look.INK)
            val ic = LineIcon(t.tag as LineIcon.Kind, if (on) Look.ON_ACCENT else Look.INK, d).apply { setBounds(0, 0, dp(20), dp(20)) }
            t.setCompoundDrawables(ic, null, null, null)
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
        col.addView(text("Wave Drop", 32f, Look.INK, bold = true))
        col.addView(text("Envoie n'importe quel fichier à un appareil proche, comme AirDrop : téléphone ou PC avec Wave Island.", 15f, Look.INK_SOFT).apply {
            setPadding(0, dp(2), 0, dp(18))
        })

        val main = card(strong = true)
        val row = switchRow(
            "Activer Wave Drop",
            text("Ton appareil devient visible des appareils Wave Drop tout proches", 13f, Look.INK_SOFT),
            com.liquidisland.drop.WaveDrop.enabled(this)
        ) { on -> if (on && !com.liquidisland.drop.WaveDrop.hasPermissions(this)) {
                requestPermissions(com.liquidisland.drop.WaveDrop.permissions(), DROP_PERMS)
            } else enableDrop(on)
        }
        dropSwitch = (row as LinearLayout).getChildAt(1) as Switch
        main.addView(row)
        main.addView(divider())
        main.addView(text("Nom de cet appareil", 13f, Look.INK_SOFT).apply { setPadding(dp(4), dp(8), 0, dp(4)) })
        val name = android.widget.EditText(this).apply {
            setText(com.liquidisland.drop.WaveDrop.myName)
            setTextColor(Look.INK)
            textSize = 16f
            setSingleLine()
            background = Look.soft(dpf(18))
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
        dropStatus = text("", 13f, Look.INK_SOFT).apply { setPadding(dp(4), dp(12), dp(4), 0) }
        main.addView(dropStatus)
        main.addView(glassButton("Envoyer un fichier", primary = true) { com.liquidisland.drop.WaveDropActivity.open(this) }, buttonLp(top = 14))
        main.addView(text("Astuce : glisse l'îlot vers le bas pour ouvrir Wave Drop depuis n'importe où.", 12f, Look.INK_SOFT).apply {
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
            14f, Look.INK_SOFT
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
        // Sans ca, Android (surtout HyperOS) endort l'appli : les fichiers arrivent en retard ou jamais
        if (on && intent?.getStringExtra("ci") == null) try {
            val pm = getSystemService(Context.POWER_SERVICE) as android.os.PowerManager
            if (!pm.isIgnoringBatteryOptimizations(packageName))
                startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        } catch (e: Exception) { }
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
        if (items.isEmpty()) dropHistory.addView(text("Rien reçu pour l'instant.", 14f, Look.INK_SOFT))
        items.take(10).forEach { h ->
            val r = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(4), dp(8), dp(4), dp(8))
                background = Look.pressable(ColorDrawable(Color.TRANSPARENT), dpf(14))
                isClickable = true
                setOnClickListener { h.uri?.let { com.liquidisland.drop.WaveDropActivity.openFile(this@MainActivity, Uri.parse(it), null) } }
            }
            r.addView(text(h.name, 15f, Look.INK).apply { maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.MIDDLE })
            r.addView(text("De ${h.from} · " + android.text.format.DateUtils.getRelativeTimeSpanString(h.time), 12f, Look.INK_SOFT))
            dropHistory.addView(r)
        }
    }

    private fun refreshDropNearby() {
        if (!::dropNearby.isInitialized) return
        dropNearby.removeAllViews()
        val wd = com.liquidisland.drop.WaveDrop
        val list = if (wd.running) wd.nearbyList() else emptyList()
        if (list.isEmpty()) dropNearby.addView(text(if (wd.running) "Aucun appareil Wave Drop autour." else "Active Wave Drop pour voir les appareils autour.", 14f, Look.INK_SOFT))
        list.take(6).forEach { n ->
            dropNearby.addView(text(n.label + "   ·   " +
                (if (n.close) "tout près" else "à proximité") + (if (n.beacon.ip != null) " · Wi-Fi" else " · Bluetooth"), 14f, Look.INK).apply {
                setPadding(dp(4), dp(6), dp(4), dp(6))
                compoundDrawablePadding = dp(10)
                setCompoundDrawables(LineIcon(if (n.beacon.isPc) LineIcon.Kind.LAPTOP else LineIcon.Kind.PHONE, Look.INK, d).apply { setBounds(0, 0, dp(22), dp(22)) }, null, null, null)
            })
        }
    }

    // ---------- Volume ----------

    private fun volumeCard(): View {
        val c = card()
        c.addView(switchRow(
            "Indicateur de volume façon iPhone",
            text("Une capsule en verre à côté des boutons, à la place de celle du téléphone. Touche-la pour régler au doigt.", 13f, Look.INK_SOFT),
            Prefs.volumeHud(this)
        ) {
            Prefs.setVolumeHud(this, it)
            if (it) IslandService.instance?.previewVolume()
        })
        c.addView(text("Côté des boutons de volume", 14f, Look.INK_SOFT).apply { setPadding(dp(4), dp(12), 0, dp(6)) })
        val seg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Look.soft(dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val sides = listOf("left" to "Gauche", "right" to "Droite")
        val views = mutableListOf<TextView>()
        fun paint() {
            val cur = Prefs.volumeSide(this)
            sides.forEachIndexed { i, (key, _) ->
                val on = key == cur
                views[i].background = if (on) Look.tinted(d, dpf(18)) else null
                views[i].setTextColor(if (on) Look.ON_ACCENT else Look.INK)
            }
        }
        sides.forEach { (key, label) ->
            val t = text(label, 14f, Look.INK).apply {
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
        c.addView(text("Appuie sur un bouton de volume pour voir l'indicateur. Il demande l'accessibilité.", 12f, Look.INK_SOFT).apply {
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
            14f, Look.INK_SOFT
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
                if (item.tintIcon) imageTintList = ColorStateList.valueOf(Look.INK)
                val p = if (item.tintIcon) dp(6) else 0
                setPadding(p, p, p, p)
                background = Look.chip(Look.ACCENT, dpf(18))
            }, LinearLayout.LayoutParams(dp(36), dp(36)).apply { marginEnd = dp(14) })
            row.addView(text(item.label, 15f, Look.INK), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            if (i > 0) row.addView(smallButton("↑") {
                val l = ids.toMutableList(); l.removeAt(i); l.add(i - 1, id); saveShortcuts(l)
            })
            row.addView(smallButton("✕") {
                saveShortcuts(ids.filterIndexed { j, _ -> j != i })
            })
            shortcutsList.addView(row)
            if (i < ids.size - 1) shortcutsList.addView(divider())
        }
        if (ids.isEmpty()) shortcutsList.addView(text("Aucun raccourci pour l'instant.", 14f, Look.INK_SOFT))
    }

    private lateinit var dockList: LinearLayout

    /** Les applis du dock (Android), 4 au maximum, dans l'ordre. */
    private fun renderDock() {
        dockList.removeAllViews()
        val ids = WaveDock.apps(this)
        ids.forEachIndexed { i, pkg ->
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(2), dp(6), 0, dp(6)) }
            row.addView(android.widget.ImageView(this).apply {
                try { setImageDrawable(packageManager.getApplicationIcon(pkg)) } catch (e: Exception) { }
            }, LinearLayout.LayoutParams(dp(34), dp(34)).apply { marginEnd = dp(14) })
            val label = try { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() } catch (e: Exception) { pkg }
            row.addView(text(label, 15f, Look.INK), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            if (i > 0) row.addView(smallButton("↑") {
                val l = ids.toMutableList(); l.removeAt(i); l.add(i - 1, pkg); Prefs.setDockApps(this, l); renderDock()
            })
            row.addView(smallButton("✕") {
                val l = ids.toMutableList(); l.remove(pkg)
                Prefs.setDockApps(this, if (l.isEmpty()) listOf("-") else l); renderDock()
            })
            dockList.addView(row)
        }
        if (ids.isEmpty()) dockList.addView(text("Aucune appli : ajoute-en jusqu'à ${WaveDock.MAX}.", 14f, Look.INK_SOFT))
    }

    private fun addDockDialog() {
        val current = WaveDock.apps(this)
        if (current.size >= WaveDock.MAX) {
            Toast.makeText(this, "Le dock a déjà ${WaveDock.MAX} applis : retires-en une d'abord", Toast.LENGTH_SHORT).show()
            return
        }
        val choices = Shortcuts.launchableApps(this).map { it.first.removePrefix("app:") to it.second }.filter { it.first !in current }
        android.app.AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("Ajouter au dock")
            .setItems(choices.map { it.second }.toTypedArray()) { _, w -> Prefs.setDockApps(this, current + choices[w].first); renderDock() }
            .setNegativeButton("Annuler", null)
            .show()
    }

    private fun smallButton(label: String, action: () -> Unit) = text(label, 16f, Look.INK_SOFT).apply {
        gravity = Gravity.CENTER
        background = Look.pressable(ColorDrawable(Color.TRANSPARENT), dpf(18))
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
        updateText = text("", 15f, Look.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) }
        updateCard.addView(updateText)
        updateBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progressTintList = ColorStateList.valueOf(Look.ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(Look.TRACK_OFF)
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
                true -> setChip(r.chip, "OK", Look.OK)
                false -> setChip(r.chip, "À faire", Look.WARN)
                null -> setChip(r.chip, "Ouvrir", Look.ACCENT)
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
        segIsland.background = if (!v) Look.tinted(d, dpf(18)) else null
        segIsland.setTextColor(if (!v) Look.ON_ACCENT else Look.INK)
        segNotch.background = if (v) Look.tinted(d, dpf(18)) else null
        segNotch.setTextColor(if (v) Look.ON_ACCENT else Look.INK)
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
            background = Look.soft(dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        fun segItem(label: String, notch: Boolean) = text(label, 15f, Look.INK).apply {
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
        c.addView(text("Teinte du verre", 14f, Look.INK_SOFT).apply { setPadding(dp(4), dp(14), 0, dp(6)) })
        val themeSeg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Look.soft(dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val themes = listOf("clear" to "Transparent", "dark" to "Sombre", "light" to "Claire")
        val themeViews = mutableListOf<TextView>()
        fun paintThemes() {
            val cur = Prefs.theme(this)
            themes.forEachIndexed { i, (key, _) ->
                val on = key == cur
                themeViews[i].background = if (on) Look.tinted(d, dpf(18)) else null
                themeViews[i].setTextColor(if (on) Look.ON_ACCENT else Look.INK)
            }
        }
        themes.forEach { (key, label) ->
            val t = text(label, 14f, Look.INK).apply {
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
        c.addView(text("Touche l'aperçu pour voir l'îlot ouvert", 12f, Look.INK_SOFT).apply {
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
        // Effet du verre (ilot et dock)
        c.addView(text("Effet du verre (îlot et dock)", 14f, Look.INK_SOFT).apply { setPadding(dp(4), dp(14), 0, dp(6)) })
        val fxSeg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Look.soft(dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val fxModes = listOf("blur" to "Flou liquide", "clear" to "Transparent", "live" to "Déformation")
        val fxViews = mutableListOf<TextView>()
        val fxHelp = text("", 12.5f, Look.INK_SOFT).apply { setPadding(dp(4), dp(6), dp(4), 0) }
        fun paintFx() {
            val cur = Prefs.glassMode(this)
            fxModes.forEachIndexed { i, (key, _) ->
                val on = key == cur
                fxViews[i].background = if (on) Look.tinted(d, dpf(18)) else null
                fxViews[i].setTextColor(if (on) Look.ON_ACCENT else Look.INK)
            }
            fxHelp.text = when (cur) {
                "blur" -> "Verre givré léger flouté par Android derrière l'îlot et le dock, avec bords liquides, reflets et lumière qui suit l'inclinaison. Sans enregistrement de l'écran."
                "clear" -> "Verre totalement transparent : seulement les bords liquides et les reflets. Sans enregistrement de l'écran."
                else -> "Le bord déforme vraiment ce qui est derrière (comme une goutte d'eau). Demande l'enregistrement de l'écran (Android 13+)."
            }
        }
        fxModes.forEach { (key, label) ->
            val t = text(label, 14f, Look.INK).apply {
                gravity = Gravity.CENTER
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isClickable = true
                setOnClickListener {
                    Prefs.setGlassMode(this@MainActivity, key)
                    paintFx()
                    IslandService.instance?.applySettings()
                    IslandService.instance?.updateStrip()
                    if (key == "live") askLive()
                }
            }
            fxViews.add(t)
            fxSeg.addView(t, LinearLayout.LayoutParams(0, dp(36), 1f))
        }
        c.addView(fxSeg)
        c.addView(fxHelp)
        paintFx()
        // En plein ecran
        c.addView(text("En plein écran (vidéo, jeu)", 14f, Look.INK_SOFT).apply { setPadding(dp(4), dp(14), 0, dp(6)) })
        val fsSeg = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = Look.soft(dpf(20))
            setPadding(dp(3), dp(3), dp(3), dp(3))
        }
        val fsModes = listOf("show" to "Contre la caméra", "hide" to "Cacher l'îlot")
        val fsViews = mutableListOf<TextView>()
        fun paintFs() {
            val cur = Prefs.fullscreen(this)
            fsModes.forEachIndexed { i, (key, _) ->
                val on = key == cur
                fsViews[i].background = if (on) Look.tinted(d, dpf(18)) else null
                fsViews[i].setTextColor(if (on) Look.ON_ACCENT else Look.INK)
            }
        }
        fsModes.forEach { (key, label) ->
            val t = text(label, 14f, Look.INK).apply {
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
        head.addView(text(label, 15f, Look.INK).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val valueText = text("$value $unit", 14f, Look.ACCENT)
        head.addView(valueText)
        box.addView(head)
        val bar = SeekBar(this).apply {
            this.max = max - min
            progress = value - min
            progressTintList = ColorStateList.valueOf(Look.ACCENT)
            thumbTintList = ColorStateList.valueOf(Look.ACCENT)
            progressBackgroundTintList = ColorStateList.valueOf(Look.TRACK_OFF)
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

    private fun linkCard(): View {
        val c = card()
        c.addView(text("Lie ton PC une seule fois (même Wi-Fi, Wave Drop allumé des deux côtés) : batterie du téléphone sur le PC, " +
            "copier sur l'un et coller sur l'autre, faire sonner l'autre, continuité des pages web, rappels partagés et résumé du matin.", 13f, Look.INK_SOFT))
        val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        fun refresh() {
            list.removeAllViews()
            com.liquidisland.drop.WaveDrop.init(this)
            com.liquidisland.drop.WaveLink.start(this)
            for (d in com.liquidisland.drop.WaveLink.linked()) {
                val id = d.id
                list.addView(glassButton("Délier ${d.name}", primary = false) { com.liquidisland.drop.WaveLink.unlink(id); refreshLink?.invoke() }, buttonLp(top = 8))
            }
            for (n in com.liquidisland.drop.WaveDrop.nearbyList().filter { it.beacon.isPc && !com.liquidisland.drop.WaveLink.isLinked(it.beacon.id) }) {
                list.addView(glassButton("Lier ${n.label}", primary = true) {
                    com.liquidisland.drop.WaveLink.pair(n) { msg -> android.widget.Toast.makeText(this, msg, android.widget.Toast.LENGTH_LONG).show(); refreshLink?.invoke() }
                }, buttonLp(top = 8))
            }
            if (list.childCount == 0) list.addView(text("Aucun PC proche : allume Wave Drop sur le PC (même Wi-Fi), puis touche « Actualiser ».", 13f, Look.INK_SOFT).apply { setPadding(dp(4), dp(8), 0, 0) })
        }
        refreshLink = { refresh() }
        refresh()
        c.addView(list)
        c.addView(glassButton("Actualiser", primary = false) { refresh() }, buttonLp(top = 8))
        c.addView(glassButton("Faire sonner mon PC", primary = false) {
            com.liquidisland.drop.WaveLink.ringPc { android.widget.Toast.makeText(this, it, android.widget.Toast.LENGTH_SHORT).show() }
        }, buttonLp(top = 8))
        c.addView(glassButton("Envoyer le presse-papiers au PC", primary = false) {
            startActivity(Intent(this, com.liquidisland.drop.ClipToPcActivity::class.java))
        }, buttonLp(top = 8))
        c.addView(switchRow("Presse-papiers partagé", text("Le texte copié sur le PC arrive ici. Pour l'autre sens : pastille « Copier vers le PC » ou Partager > Copier vers le PC.", 13f, Look.INK_SOFT),
            com.liquidisland.drop.WaveLink.clipSync()) { com.liquidisland.drop.WaveLink.setClipSync(it) })
        c.addView(switchRow("Continuité", text("La page ouverte dans ton navigateur est proposée sur le PC (et l'inverse).", 13f, Look.INK_SOFT),
            com.liquidisland.drop.WaveLink.handoff()) { com.liquidisland.drop.WaveLink.setHandoff(it) })
        c.addView(switchRow("Résumé du matin", text("Au premier déverrouillage de la journée : météo et rappels du jour.", 13f, Look.INK_SOFT),
            com.liquidisland.drop.WaveLink.morning()) { com.liquidisland.drop.WaveLink.setMorning(it) })
        return c
    }

    private var refreshLink: (() -> Unit)? = null

    private fun assistCard(): View {
        val c = card()
        c.addView(text("Appui long sur le bouton d'alimentation (comme pour Gemini) : la Wave Island t'écoute et Gemini répond à voix haute. " +
            "Il peut chercher sur Firefox, ouvrir une appli ou un site, régler le son, la luminosité, la lampe, la musique, un minuteur, une alarme, appeler ou envoyer un SMS. " +
            "Touche l'îlot pendant qu'il écoute pour envoyer tout de suite, ou pendant qu'il parle pour poser une autre question.", 13f, Look.INK_SOFT))
        c.addView(text("Clé Gemini gratuite (aistudio.google.com > Get API key)", 14f, Look.INK).apply { setPadding(dp(4), dp(12), 0, dp(4)) })
        val key = android.widget.EditText(this).apply {
            setText(Prefs.aiKey(this@MainActivity))
            hint = "AIza…"
            setSingleLine()
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
            setTextColor(Look.INK)
            setHintTextColor(Look.INK_SOFT)
            textSize = 15f
            background = Look.soft(dpf(18))
            setPadding(dp(14), dp(10), dp(14), dp(10))
            addTextChangedListener(object : android.text.TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(s: android.text.Editable?) { Prefs.setAiKey(this@MainActivity, s?.toString() ?: "") }
            })
        }
        c.addView(key, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        c.addView(glassButton("Créer une clé Gemini gratuite", primary = false) {
            try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://aistudio.google.com/apikey"))) } catch (e: Exception) { }
        }, buttonLp(top = 8))
        val sonnet = Prefs.aiModel(this).contains("pro")
        c.addView(switchRow("Modèle plus malin (Gemini Pro)",
            text("Sinon Flash : plus rapide, avec plus de questions gratuites par jour.", 13f, Look.INK_SOFT), sonnet
        ) { Prefs.setAiModel(this, if (it) "gemini-pro-latest" else "gemini-flash-latest") })
        c.addView(switchRow("Réponse à voix haute",
            text("Une voix française lit la réponse (sinon elle s'affiche seulement dans l'îlot).", 13f, Look.INK_SOFT), Prefs.aiVoice(this)
        ) { Prefs.setAiVoice(this, it) })
        c.addView(glassButton("Autoriser micro, contacts, appels et SMS", primary = false) {
            requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO, android.Manifest.permission.READ_CONTACTS,
                android.Manifest.permission.CALL_PHONE, android.Manifest.permission.SEND_SMS), 41)
        }, buttonLp(top = 8))
        c.addView(text("Pour que l'appui long ouvre Wave Island à la place de Gemini : choisis « Wave Island » comme application d'assistant numérique. " +
            "Sur Xiaomi, vérifie aussi Réglages > Réglages supplémentaires > Raccourcis gestuels > « Lancer l'assistant » (appui long sur le bouton).", 13f, Look.INK_SOFT).apply { setPadding(dp(4), dp(12), 0, dp(4)) })
        c.addView(glassButton("Choisir Wave Island comme assistant", primary = true) { openAssistantSettings() }, buttonLp(top = 8))
        c.addView(glassButton("Essayer maintenant", primary = false) {
            val island = IslandService.instance
            if (island != null) island.startAssist() else com.liquidisland.assist.Assistant.trigger(this)
        }, buttonLp(top = 8))
        return c
    }

    /** Ecran « Assistant numerique » d'Android (selon les telephones, plusieurs chemins possibles). */
    private fun openAssistantSettings() {
        val tries = listOf(
            Intent(Settings.ACTION_VOICE_INPUT_SETTINGS),
            Intent("android.settings.MANAGE_DEFAULT_APPS_SETTINGS"),
            Intent(Settings.ACTION_SETTINGS)
        )
        for (i in tries) {
            try { startActivity(i); return } catch (e: Exception) { }
        }
    }

    private var sectionCount = 0

    /** Titre de rubrique : pastille ronde coloree avec une icone, puis le titre en gras. */
    private fun section(title: String): View {
        val t = title.lowercase()
        val kind = when {
            "apparence" in t -> LineIcon.Kind.ISLAND
            "volume" in t -> LineIcon.Kind.VOLUME
            "activit" in t -> LineIcon.Kind.PLAY
            "link" in t -> LineIcon.Kind.LAPTOP
            "assistant" in t -> LineIcon.Kind.BELL
            "menu" in t -> LineIcon.Kind.APPS
            "xiaomi" in t -> LineIcon.Kind.PHONE
            "autorisation" in t -> LineIcon.Kind.LOCK
            "grisé" in t || "restreint" in t -> LineIcon.Kind.SETTINGS
            "option" in t -> LineIcon.Kind.SETTINGS
            "mise" in t -> LineIcon.Kind.UPDATE
            "appareils" in t -> LineIcon.Kind.PHONE
            "reçus" in t -> LineIcon.Kind.DOWNLOAD
            "comment" in t -> LineIcon.Kind.CHECK
            else -> LineIcon.Kind.DROP
        }
        val color = Look.PALETTE[sectionCount++ % Look.PALETTE.size]
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(4), dp(28), 0, dp(12))
        }
        row.addView(android.widget.ImageView(this).apply {
            background = Look.bubble(color)
            setImageDrawable(LineIcon(kind, Color.WHITE, d))
            setPadding(dp(8), dp(8), dp(8), dp(8))
            outlineProvider = android.view.ViewOutlineProvider.BACKGROUND
            clipToOutline = true
            elevation = dpf(3)
        }, LinearLayout.LayoutParams(dp(34), dp(34)).apply { marginEnd = dp(12) })
        row.addView(text(title, 19f, Look.INK, bold = true))
        return row
    }

    private fun card(strong: Boolean = false) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = Look.card(d, dpf(26), strong)
        setPadding(dp(18), dp(16), dp(18), dp(16))
        elevation = dpf(2)
    }

    private fun divider() = View(this).apply {
        setBackgroundColor(Look.LINE)
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
                    setTextColor(Look.ON_ACCENT)
                    background = Look.pressable(Look.tinted(d, r), r, dark = true)
                }
                onWhite -> {
                    setTextColor(Look.ACCENT_DEEP)
                    background = Look.pressable(GradientDrawable().apply { cornerRadius = r; setColor(Color.WHITE) }, r)
                }
                else -> {
                    setTextColor(Look.ACCENT_DEEP)
                    background = Look.pressable(GradientDrawable().apply { cornerRadius = r; setColor(Color.rgb(238, 236, 255)) }, r)
                }
            }
            isClickable = true
            setOnClickListener { action() }
        }

    private fun heroCard(): View {
        val c = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = Look.gradient(dpf(30))
            setPadding(dp(20), dp(20), dp(20), dp(20))
            elevation = dpf(8)
        }
        c.addView(text("L'îlot", 22f, Color.WHITE, bold = true))
        heroStatus = text("", 15f, Color.argb(225, 255, 255, 255))
        c.addView(heroStatus)

        // Sans l'accessibilite, la barre d'etat recoit les touchers a la place de l'ilot
        accessWarn = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = Look.card(d, dpf(20), strong = true)
            setPadding(dp(14), dp(12), dp(14), dp(12))
            visibility = View.GONE
        }
        accessTitle = text("Toucher l'îlot ouvre le volet des réglages ?", 15f, Look.INK, bold = true)
        accessWarn.addView(accessTitle)
        accessBody = text("", 13f, Look.INK_SOFT).apply { setPadding(0, dp(4), 0, 0) }
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
        chip.background = Look.chip(color, dpf(12))
    }

    private fun permRow(num: String, title: String, sub: String, check: () -> Boolean?, action: () -> Unit): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(4), dp(10), dp(4), dp(10))
            background = Look.pressable(ColorDrawable(Color.TRANSPARENT), dpf(16))
            isClickable = true
            setOnClickListener { action() }
        }
        if (num.isNotEmpty()) {
            row.addView(text(num, 14f, Look.ACCENT, bold = true).apply {
                gravity = Gravity.CENTER
                background = Look.chip(Look.ACCENT, dpf(15))
                layoutParams = LinearLayout.LayoutParams(dp(30), dp(30)).apply { marginEnd = dp(14) }
            })
        }
        val texts = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            addView(text(title, 15f, Look.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) })
            addView(text(sub, 13f, Look.INK_SOFT))
        }
        row.addView(texts)
        val chip = text("", 12f, Look.ACCENT).apply {
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
            addView(text(title, 15f, Look.INK).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) })
            addView(sub)
        }
        row.addView(texts)
        val states = arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf())
        val sw = Switch(this).apply {
            isChecked = initial
            thumbTintList = ColorStateList(states, intArrayOf(Color.WHITE, Color.WHITE))
            trackTintList = ColorStateList(states, intArrayOf(Look.ACCENT, Look.TRACK_OFF))
            setOnCheckedChangeListener { _, v -> onChange(v) }
        }
        row.addView(sw)
        row.setOnClickListener { sw.toggle() }
        return row
    }
}
