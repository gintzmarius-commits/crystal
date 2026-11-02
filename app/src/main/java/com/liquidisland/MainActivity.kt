package com.liquidisland

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast

class MainActivity : Activity() {

    private lateinit var status: TextView

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(40), dp(20), dp(24))
        }

        col.label("Liquid Island", 26f, true)
        col.label("Un ilot dynamique en verre, toujours affiche, qui remplace les notifications.", 14f, false)

        status = col.label("", 14f, false)

        col.label("\nEtape 1 : autorisations", 16f, true)
        col.btn("1. Autoriser l'affichage par-dessus les applis") {
            open(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        }
        col.btn("2. Autoriser l'acces aux notifications") {
            open(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        col.label(
            "Si Android refuse (parametre restreint) : Reglages > Applications > Liquid Island > menu (3 points) > Autoriser les parametres restreints.",
            12f, false
        )
        col.btn("3. Ne pas mettre l'appli en veille (batterie)") {
            open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        }
        col.btn("4. Ouvrir les reglages (couper les notifications flottantes)") {
            open(Intent(Settings.ACTION_SETTINGS))
        }
        col.label(
            "Samsung : Notifications > Style de notification pop-up. Xiaomi : active aussi le demarrage automatique et verrouille l'appli dans les applis recentes.",
            12f, false
        )

        col.label("\nEtape 2 : options", 16f, true)
        col.sw("Masquer les notifications du systeme", Prefs.hideSystem(this)) {
            Prefs.setHideSystem(this, it)
        }
        col.sw("Flou derriere l'ilot (experimental, Android 12+)", Prefs.blur(this)) {
            Prefs.setBlur(this, it)
            restartIsland()
        }

        col.label("\nEtape 3 : lancer", 16f, true)
        col.btn("Demarrer l'ilot") { startIsland() }
        col.btn("Envoyer une notification de test") { sendTest() }
        col.btn("Arreter l'ilot") {
            Prefs.setEnabled(this, false)
            stopService(Intent(this, IslandService::class.java))
            refresh()
        }

        setContentView(ScrollView(this).apply { addView(col) })

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    private fun refresh() {
        val overlay = Settings.canDrawOverlays(this)
        val listener = Settings.Secure.getString(contentResolver, "enabled_notification_listeners")
            ?.contains(packageName) == true
        val battery = (getSystemService(Context.POWER_SERVICE) as PowerManager)
            .isIgnoringBatteryOptimizations(packageName)
        status.text = "Affichage par-dessus : ${mark(overlay)}\n" +
            "Acces aux notifications : ${mark(listener)}\n" +
            "Batterie (pas de veille) : ${mark(battery)}\n" +
            "Ilot : ${if (Prefs.enabled(this)) "demarre" else "arrete"}"
    }

    private fun mark(ok: Boolean) = if (ok) "OK" else "a faire"

    private fun startIsland() {
        if (!Settings.canDrawOverlays(this)) {
            Toast.makeText(this, "Autorise d'abord l'affichage par-dessus les applis", Toast.LENGTH_LONG).show()
            return
        }
        Prefs.setEnabled(this, true)
        startForegroundService(Intent(this, IslandService::class.java))
        refresh()
    }

    private fun restartIsland() {
        if (Prefs.enabled(this)) {
            stopService(Intent(this, IslandService::class.java))
            Handler(Looper.getMainLooper()).postDelayed({ startIsland() }, 400)
        }
    }

    private fun sendTest() {
        if (!Prefs.enabled(this)) startIsland()
        Handler(Looper.getMainLooper()).postDelayed({
            IslandBus.post(
                IslandNote(
                    "Liquid Island",
                    "Ceci est une notification de test.",
                    applicationInfo.loadIcon(packageManager),
                    null
                )
            )
        }, 700)
    }

    private fun open(intent: Intent) {
        try {
            startActivity(intent)
        } catch (e: Exception) {
            Toast.makeText(this, "Impossible d'ouvrir ce reglage", Toast.LENGTH_SHORT).show()
        }
    }

    private fun LinearLayout.label(text: String, size: Float, bold: Boolean): TextView {
        val tv = TextView(this@MainActivity)
        tv.text = text
        tv.textSize = size
        if (bold) tv.setTypeface(tv.typeface, android.graphics.Typeface.BOLD)
        tv.setPadding(0, dp(4), 0, dp(4))
        addView(tv)
        return tv
    }

    private fun LinearLayout.btn(text: String, action: () -> Unit) {
        val b = Button(this@MainActivity)
        b.text = text
        b.isAllCaps = false
        b.setOnClickListener { action() }
        addView(b)
    }

    private fun LinearLayout.sw(text: String, initial: Boolean, onChange: (Boolean) -> Unit) {
        val s = Switch(this@MainActivity)
        s.text = text
        s.isChecked = initial
        s.setPadding(0, dp(8), 0, dp(8))
        s.setOnCheckedChangeListener { _, v -> onChange(v) }
        addView(s)
    }
}
