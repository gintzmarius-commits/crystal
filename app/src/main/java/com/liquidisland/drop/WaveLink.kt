package com.liquidisland.drop

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.net.Uri
import android.os.BatteryManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import com.liquidisland.IslandNote
import com.liquidisland.IslandService
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/**
 * Lot 4 : Wave Link, le lien permanent entre le telephone et le PC (par Wave Drop, sur le meme Wi-Fi).
 * Une fois lies : batterie du telephone sur le PC, presse-papiers partage, faire sonner l'autre,
 * continuite des pages web, rappels partages, resume du matin.
 */
object WaveLink {
    private const val TAG = "LiquidIsland"
    private val main = Handler(Looper.getMainLooper())
    private lateinit var app: Context
    private fun prefs() = app.getSharedPreferences("wavelink", Context.MODE_PRIVATE)

    data class Device(val id: Int, val name: String, val ip: String, val port: Int)

    // ================== Appareils lies ==================

    fun linked(): List<Device> = prefs().getStringSet("linked", emptySet())!!.mapNotNull {
        val p = it.split("|")
        if (p.size < 4) null else Device(p[0].toIntOrNull() ?: return@mapNotNull null, p[1], p[2], p[3].toIntOrNull() ?: 0)
    }

    fun isLinked(id: Int) = linked().any { it.id == id }

    private fun save(id: Int, name: String?, ip: String?, port: Int) {
        val old = linked().firstOrNull { it.id == id }
        val set = prefs().getStringSet("linked", emptySet())!!.filterNot { it.startsWith("$id|") }.toMutableSet()
        set.add("$id|${(name ?: old?.name ?: "PC").replace("|", " ")}|${ip ?: old?.ip ?: ""}|${if (port > 0) port else old?.port ?: 0}")
        prefs().edit().putStringSet("linked", set).apply()
    }

    fun unlink(id: Int) {
        prefs().edit().putStringSet("linked", prefs().getStringSet("linked", emptySet())!!.filterNot { it.startsWith("$id|") }.toSet()).apply()
    }

    fun clipSync() = prefs().getBoolean("clip", true)
    fun setClipSync(v: Boolean) = prefs().edit().putBoolean("clip", v).apply()
    fun handoff() = prefs().getBoolean("handoff", true)
    fun setHandoff(v: Boolean) = prefs().edit().putBoolean("handoff", v).apply()
    fun morning() = prefs().getBoolean("morning", true)
    fun setMorning(v: Boolean) = prefs().edit().putBoolean("morning", v).apply()

    // ================== Demarrage (depuis le service de l'ilot) ==================

    private var started = false
    private var lastLevel = -10
    private var lastCharging = false

    fun start(ctx: Context) {
        app = ctx.applicationContext
        if (started) return
        started = true
        // Batterie : envoyee au PC quand elle change (3 % ou branchement) et toutes les 5 minutes
        app.registerReceiver(object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) * 100 / i.getIntExtra(BatteryManager.EXTRA_SCALE, 100).coerceAtLeast(1)
                val charging = i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0
                if (kotlin.math.abs(level - lastLevel) >= 3 || charging != lastCharging) { lastLevel = level; lastCharging = charging; sendBattery() }
            }
        }, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        main.postDelayed(object : Runnable {
            override fun run() { sendBattery(); main.postDelayed(this, 5 * 60_000L) }
        }, 20_000)
        // Rappels : verifies toutes les 20 s
        main.postDelayed(object : Runnable {
            override fun run() { checkReminders(); main.postDelayed(this, 20_000) }
        }, 5_000)
        // Deverrouillage : page du PC a continuer, resume du matin
        app.registerReceiver(object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                if (i.action == Intent.ACTION_USER_PRESENT) { offerPcPage(); morningMaybe() }
                if (i.action == Intent.ACTION_SCREEN_OFF) leftBrowser()
            }
        }, IntentFilter().apply { addAction(Intent.ACTION_USER_PRESENT); addAction(Intent.ACTION_SCREEN_OFF) })
    }

    private fun note(title: String, text: String, action: (() -> Unit)? = null) =
        main.post { IslandService.instance?.show(IslandNote(title, text, null, null, pkg = app.packageName, action = action)) }

    // ================== Reception (appelee par Wave Drop, hors du fil principal) ==================

    fun receive(m: Map<String, String>, fromId: Int, fromName: String, ip: String?): Map<String, String> {
        val t = m["t"] ?: ""
        val port = m["port"]?.toIntOrNull() ?: 0
        Log.i(TAG, "Wave Link : « $t » de $fromName")
        if (t == "pair") {
            if (isLinked(fromId)) { save(fromId, fromName, ip, port); return ok() }
            note("Lier « $fromName » ?", "Touche pour accepter : batterie, presse-papiers, sonnerie, continuité") {
                save(fromId, fromName, ip, port)
                Thread { send(fromId, msg("pair-ok")) }.start()
                note("Lié à $fromName", "Les deux appareils sont maintenant liés.")
            }
            return mapOf("ok" to "pending")
        }
        if (t == "pair-ok") {
            if (pairing.remove(fromId)) { save(fromId, fromName, ip, port); note("Lié à $fromName", "Les deux appareils sont maintenant liés.") }
            return ok()
        }
        if (!isLinked(fromId)) return mapOf("ok" to "0", "err" to "pas lie")
        if (ip != null && port > 0) linked().firstOrNull { it.id == fromId }?.let { if (it.ip != ip || it.port != port) save(fromId, fromName, ip, port) }
        when (t) {
            "ring" -> main.post { ring(fromName) }
            "clip" -> {
                val text = m["text"] ?: ""
                main.post {
                    try {
                        (app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("Wave Link", text))
                        note("Copié depuis $fromName", if (text.length > 80) text.take(80) + "…" else text)
                    } catch (e: Exception) { Log.w(TAG, "Wave Link : presse-papiers", e) }
                }
            }
            "handoff" -> {
                prefs().edit().putString("pcUrl", m["url"]).putString("pcTitle", m["title"]).putString("pcFrom", fromName)
                    .putLong("pcAt", System.currentTimeMillis()).apply()
            }
            "remind" -> addReminder(m["text"] ?: "", m["at"]?.toLongOrNull() ?: System.currentTimeMillis(), share = false)
        }
        return ok()
    }

    private fun ok() = mapOf("ok" to "1")
    fun msg(t: String, vararg kv: Pair<String, String?>): Map<String, String> = mapOf("t" to t) + kv.associate { it.first to (it.second ?: "") }

    // ================== Envoi ==================

    private val pairing = java.util.concurrent.ConcurrentHashMap.newKeySet<Int>()

    /** Demande de lien a un PC proche (il accepte en touchant son ilot). */
    fun pair(n: WaveDrop.Nearby, done: (String) -> Unit) {
        pairing.add(n.beacon.id)
        Thread {
            val r = WaveDrop.sendLink(n.beacon.ipString, n.beacon.port, msg("pair"))
            val text = when {
                r == null -> "Lien impossible : mets les deux appareils sur le même Wi-Fi, avec Wave Drop allumé."
                r["ok"] == "1" -> { pairing.remove(n.beacon.id); save(n.beacon.id, n.label, n.beacon.ipString, n.beacon.port); "Lié à ${n.label} !" }
                else -> "Demande envoyée : touche l'îlot du PC pour accepter."
            }
            main.post { done(text) }
        }.start()
    }

    /** Message a un appareil lie (adresse vue a l'instant, sinon la derniere connue). Hors du fil principal. */
    fun send(id: Int, m: Map<String, String>): Map<String, String>? {
        val near = WaveDrop.nearby[id]
        var ip = near?.beacon?.ipString
        var port = near?.beacon?.port ?: 0
        if (ip == null || port == 0) { val d = linked().firstOrNull { it.id == id }; ip = d?.ip; port = d?.port ?: 0 }
        if (ip.isNullOrEmpty() || port == 0) return null
        return WaveDrop.sendLink(ip, port, m)
    }

    fun sendAll(m: Map<String, String>): Int = linked().count { send(it.id, m) != null }

    private fun sendBattery() {
        if (!::app.isInitialized || linked().isEmpty()) return
        val bm = app.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        val level = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        val charging = bm.isCharging
        Thread { sendAll(msg("battery", "level" to level.toString(), "charging" to if (charging) "1" else "0")) }.start()
    }

    fun ringPc(done: (String) -> Unit) = Thread {
        val n = sendAll(msg("ring"))
        main.post { done(if (n > 0) "Ton PC sonne." else if (linked().isEmpty()) "Aucun PC lié." else "PC injoignable (allumé, même Wi-Fi, Wave Island ouvert).") }
    }.start()

    fun sendClip(text: String, done: (String) -> Unit) = Thread {
        val n = sendAll(msg("clip", "text" to text))
        main.post { done(if (n > 0) "Copié sur le PC" else if (linked().isEmpty()) "Aucun PC lié" else "PC injoignable") }
    }.start()

    // ================== Sonnerie (« telephone introuvable ») ==================

    private var ringtone: Ringtone? = null
    private var oldAlarm = -1

    private fun ring(from: String) {
        stopRing()
        try {
            val am = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager
            oldAlarm = am.getStreamVolume(AudioManager.STREAM_ALARM)
            am.setStreamVolume(AudioManager.STREAM_ALARM, am.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)
            val uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM) ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
            ringtone = RingtoneManager.getRingtone(app, uri)?.apply {
                audioAttributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM).build()
                if (android.os.Build.VERSION.SDK_INT >= 28) isLooping = true
                play()
            }
            main.postDelayed({ stopRing() }, 30_000)
            note("$from te cherche", "Touche l'îlot pour arrêter la sonnerie") { stopRing() }
        } catch (e: Exception) { Log.w(TAG, "Wave Link : sonnerie impossible", e) }
    }

    fun stopRing() {
        try { ringtone?.stop() } catch (e: Exception) { }
        ringtone = null
        if (oldAlarm >= 0) try { (app.getSystemService(Context.AUDIO_SERVICE) as AudioManager).setStreamVolume(AudioManager.STREAM_ALARM, oldAlarm, 0) } catch (e: Exception) { }
        oldAlarm = -1
    }

    // ================== Continuite ==================

    private var url: String? = null
    private var title: String? = null
    private var sentUrl: String? = null

    /** Le service d'accessibilite a lu la barre d'adresse du navigateur. */
    fun browserPage(u: String, t: String?) { url = if (u.contains("://")) u else "https://$u"; title = t }

    /** Le navigateur n'est plus au premier plan (ou l'ecran s'eteint) : la page part vers le PC. */
    fun leftBrowser() {
        val u = url ?: return
        if (!::app.isInitialized || !handoff() || u == sentUrl || linked().isEmpty()) return
        sentUrl = u
        Thread { if (sendAll(msg("handoff", "url" to u, "title" to title)) > 0) Log.i(TAG, "Continuite : page envoyee au PC") }.start()
    }

    /** Au deverrouillage : la page laissee sur le PC (moins de 30 min). */
    private fun offerPcPage() {
        if (!handoff()) return
        val u = prefs().getString("pcUrl", null) ?: return
        if (System.currentTimeMillis() - prefs().getLong("pcAt", 0) > 30 * 60_000L) return
        val t = prefs().getString("pcTitle", null)
        val from = prefs().getString("pcFrom", "PC")
        prefs().edit().remove("pcUrl").apply()
        note("Continuer sur le téléphone", (if (t.isNullOrBlank()) u else t) + " · depuis $from") {
            try { app.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(u)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (e: Exception) { }
        }
    }

    // ================== Rappels ==================

    /** "at|texte" ; partages avec le PC lie. */
    fun addReminder(text: String, at: Long, share: Boolean = true) {
        if (text.isBlank()) return
        val set = prefs().getStringSet("reminders", emptySet())!!.toMutableSet()
        set.add("$at|${text.replace("\n", " ")}")
        prefs().edit().putStringSet("reminders", set).apply()
        Log.i(TAG, "Rappel : « $text » a ${Date(at)}")
        if (share) Thread { sendAll(msg("remind", "text" to text, "at" to at.toString())) }.start()
    }

    /** « Quand j'ouvre le PC » : le rappel part vers le PC lie. */
    fun reminderOnPc(text: String): Boolean = sendAll(msg("remind-pc", "text" to text)) > 0

    private fun remindersToday(): Int {
        val end = Calendar.getInstance().apply { set(Calendar.HOUR_OF_DAY, 23); set(Calendar.MINUTE, 59) }.timeInMillis
        return prefs().getStringSet("reminders", emptySet())!!.count { (it.substringBefore("|").toLongOrNull() ?: 0) <= end }
    }

    private fun checkReminders() {
        val set = prefs().getStringSet("reminders", emptySet())!!
        val now = System.currentTimeMillis()
        val due = set.filter { (it.substringBefore("|").toLongOrNull() ?: 0) <= now }
        if (due.isEmpty()) return
        prefs().edit().putStringSet("reminders", set - due.toSet()).apply()
        due.forEach { note("Rappel", it.substringAfter("|")) }
    }

    // ================== Resume du matin ==================

    private fun morningMaybe() {
        if (!morning()) return
        val today = SimpleDateFormat("yyyy-MM-dd", Locale.FRANCE).format(Date())
        if (Calendar.getInstance().get(Calendar.HOUR_OF_DAY) < 5 || prefs().getString("morningDone", "") == today) return
        prefs().edit().putString("morningDone", today).apply()
        Thread {
            val parts = ArrayList<String>()
            weather()?.let { parts.add(it) }
            val r = remindersToday()
            if (r > 0) parts.add("$r rappel${if (r > 1) "s" else ""} aujourd'hui")
            val day = SimpleDateFormat("EEEE d MMMM", Locale.FRANCE).format(Date())
            note("Bonjour ! On est $day", if (parts.isEmpty()) "Belle journée !" else parts.joinToString(" · "))
        }.start()
    }

    private fun get(u: String): String {
        val c = URL(u).openConnection() as HttpURLConnection
        c.connectTimeout = 6000; c.readTimeout = 6000
        return c.inputStream.bufferedReader().use { it.readText() }.also { c.disconnect() }
    }

    private fun weather(): String? = try {
        val loc = JSONObject(get("https://ipapi.co/json/"))
        val w = JSONObject(get(String.format(Locale.US,
            "https://api.open-meteo.com/v1/forecast?latitude=%f&longitude=%f&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=1",
            loc.getDouble("latitude"), loc.getDouble("longitude"))))
        val cur = w.getJSONObject("current")
        val d = w.getJSONObject("daily")
        val code = cur.getInt("weather_code")
        val sky = when { code == 0 -> "ciel dégagé"; code <= 3 -> "quelques nuages"; code <= 48 -> "brouillard"; code <= 67 -> "pluie"; code <= 77 -> "neige"; code <= 82 -> "averses"; else -> "orages" }
        val rain = d.getJSONArray("precipitation_probability_max").optInt(0, 0)
        "${loc.optString("city")} ${Math.round(cur.getDouble("temperature_2m"))}°, $sky (de ${Math.round(d.getJSONArray("temperature_2m_min").getDouble(0))}° à ${Math.round(d.getJSONArray("temperature_2m_max").getDouble(0))}°${if (rain >= 40) ", pluie $rain %" else ""})"
    } catch (e: Exception) { Log.i(TAG, "Meteo indisponible : $e"); null }
}

/**
 * « Copier vers le PC » (pastille des reglages rapides, bouton de l'appli, ou partage d'un texte) :
 * Android ne laisse lire le presse-papiers qu'a une appli visible, d'ou cette fenetre invisible d'un instant.
 */
class ClipToPcActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WaveDrop.init(this)
        WaveLink.start(this)
        intent?.getStringExtra(Intent.EXTRA_TEXT)?.let { send(it); return }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || isFinishing || intent?.hasExtra(Intent.EXTRA_TEXT) == true) return
        val clip = (getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
        if (text.isNullOrBlank()) { Toast.makeText(this, "Le presse-papiers est vide", Toast.LENGTH_SHORT).show(); finish(); return }
        send(text)
    }

    private fun send(text: String) {
        WaveLink.sendClip(text) { Toast.makeText(applicationContext, it, Toast.LENGTH_SHORT).show() }
        finish()
    }
}

/** Pastille « Copier vers le PC ». */
class ClipTile : android.service.quicksettings.TileService() {
    override fun onStartListening() {
        val t = qsTile ?: return
        t.label = "Copier vers le PC"
        t.state = android.service.quicksettings.Tile.STATE_INACTIVE
        t.icon = android.graphics.drawable.Icon.createWithResource(this, com.liquidisland.R.drawable.ic_tile_drop)
        t.updateTile()
    }

    override fun onClick() {
        val i = Intent(this, ClipToPcActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (android.os.Build.VERSION.SDK_INT >= 34)
            startActivityAndCollapse(android.app.PendingIntent.getActivity(this, 1, i, android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT))
        else @Suppress("DEPRECATION") startActivityAndCollapse(i)
    }
}
