package com.liquidisland.assist

import android.Manifest
import android.annotation.SuppressLint
import android.app.SearchManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.hardware.camera2.CameraManager
import android.media.AudioManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.AlarmClock
import android.provider.ContactsContract
import android.provider.Settings
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.telephony.SmsManager
import android.util.Log
import android.view.KeyEvent
import com.liquidisland.IslandService
import com.liquidisland.Prefs
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.text.Normalizer
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

/**
 * Version finale : l'assistant vocal de la Wave Island (Claude), sur telephone.
 * Appui long sur le bouton (assistant par defaut) ou bouton de l'appli : l'ilot s'ouvre et ecoute,
 * Claude repond (texte dans l'ilot + voix) et peut agir : sites, recherches, applis, son, musique,
 * luminosite, lampe, minuteur, alarme, appels et SMS.
 */
object Assistant {
    private const val TAG = "LiquidIsland"

    enum class State { IDLE, LISTENING, THINKING, SPEAKING, DONE }

    @Volatile var state = State.IDLE
        private set

    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private var heard = ""
    private val history = ArrayList<JSONObject>()
    private var lastTurn = 0L
    private var noWebSearch = false
    private var gen = 0
    private var tts: TextToSpeech? = null
    private var ttsReady = false
    private lateinit var app: Context

    private fun show(status: String, answer: String? = null, done: Boolean = false) {
        IslandService.instance?.showAssist(status, heard, answer, state, done)
    }

    // ================== Declenchement ==================

    /** Appui long / bouton : au repos = ecoute ; pendant l'ecoute = envoie ; pendant la voix = coupe et reecoute. */
    fun trigger(ctx: Context) {
        app = ctx.applicationContext
        Log.i(TAG, "Assistant : declenche ($state)")
        when (state) {
            State.LISTENING -> AssistActivity.current?.stopListening()
            State.THINKING -> {}
            State.SPEAKING -> { stopSpeaking(); listen() }
            else -> listen()
        }
    }

    /** L'ilot s'est referme : on arrete tout. */
    fun close() {
        gen++
        AssistActivity.current?.finishQuiet()
        stopSpeaking()
        state = State.IDLE
    }

    private fun listen() {
        if (Prefs.aiKey(app).isBlank()) {
            state = State.DONE
            heard = ""
            show("Assistant à configurer", "Ouvre Wave Island et colle ta clé Gemini gratuite (aistudio.google.com/apikey) dans « Assistant IA ».", done = true)
            return
        }
        if (SystemClock.elapsedRealtime() - lastTurn > 5 * 60_000L) history.clear()
        gen++
        state = State.LISTENING
        heard = ""
        show("Je t'écoute…")
        // L'ecoute se fait dans une petite fenetre transparente (Android ne donne le micro qu'a une appli visible)
        val i = Intent(app, AssistActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
        try { app.startActivity(i) } catch (e: Exception) { failed("Impossible d'ouvrir le micro : ${e.message}") }
    }

    // Appele par AssistActivity
    fun partial(text: String) { if (state == State.LISTENING) { heard = text; show("Je t'écoute…") } }

    fun heardDone(text: String) {
        if (state != State.LISTENING) return
        if (text.isBlank()) { state = State.DONE; show("Je n'ai rien entendu", null, done = true); return }
        heard = text.trim()
        Log.i(TAG, "Assistant : « $heard »")
        ask(heard)
    }

    fun failed(msg: String) {
        if (state != State.LISTENING) return
        state = State.DONE
        show("Micro indisponible", msg, done = true)
    }

    // ================== Claude ==================

    private fun ask(q: String) {
        state = State.THINKING
        show("Je réfléchis…")
        val myGen = ++gen
        io.execute {
            val result = try { Result.success(run(q)) } catch (e: Exception) { Result.failure(e) }
            main.post {
                if (myGen != gen) return@post
                result.onFailure {
                    Log.i(TAG, "Assistant : erreur ${it.message}")
                    state = State.DONE
                    show("Oups", it.message ?: "Erreur", done = true)
                }
                result.onSuccess { raw ->
                    val answer = clean(raw).ifBlank { "C'est fait." }
                    lastTurn = SystemClock.elapsedRealtime()
                    history.add(JSONObject().put("role", "user").put("content", q))
                    history.add(JSONObject().put("role", "assistant").put("content", answer))
                    while (history.size > 16) history.removeAt(0)
                    Log.i(TAG, "Assistant : reponse : $answer")
                    if (Prefs.aiVoice(app)) {
                        state = State.SPEAKING
                        show("", answer)
                        speak(answer) {
                            if (myGen != gen || state != State.SPEAKING) return@speak
                            // Question en retour (« Je l'envoie ? ») : on ecoute la reponse tout de suite
                            if (answer.trimEnd().endsWith("?")) listen()
                            else { state = State.DONE; show("", answer, done = true) }
                        }
                    } else {
                        state = State.DONE
                        show("", answer, done = true)
                    }
                }
            }
        }
    }

    private fun model() = Prefs.aiModel(app).let { if (it.startsWith("gemini")) it else "gemini-flash-latest" }

    private fun systemPrompt(): String {
        val now = Date()
        val day = SimpleDateFormat("EEEE d MMMM yyyy", Locale.FRANCE).format(now)
        val time = SimpleDateFormat("HH:mm", Locale.FRANCE).format(now)
        return "Tu es l'assistant vocal intégré à Wave Island sur le téléphone Android de l'utilisateur (un îlot en verre liquide en haut de l'écran). " +
            "Tu réponds en français, à l'oral : une à trois phrases courtes et naturelles, sans markdown, sans listes, sans emojis, sans liens. " +
            "Quand on te demande d'agir (ouvrir un site, chercher quelque chose, lancer une appli, régler le son, la luminosité, la lampe, la musique, un minuteur, une alarme, appeler quelqu'un), " +
            "utilise directement l'outil adapté puis confirme en une courte phrase. " +
            "Pour appeler ou écrire à quelqu'un par son nom, cherche d'abord le contact avec find_contact. " +
            "Avant d'envoyer un SMS, lis le texte exact et le destinataire et demande « Je l'envoie ? » : n'utilise send_sms qu'après un oui clair. " +
            "Pour une question d'actualité, de météo, de prix ou un fait récent, utilise la recherche web si elle est disponible. " +
            "Si on te demande de chercher « sur » un navigateur ou un site (Firefox, Chrome, YouTube...), ouvre la recherche avec l'outil. " +
            "Nous sommes le $day, il est $time."
    }

    private fun str(d: String) = JSONObject().put("type", "string").put("description", d)
    private fun en(vararg v: String) = JSONObject().put("type", "string").put("enum", JSONArray(v.toList()))
    private fun int(min: Int, max: Int? = null) = JSONObject().put("type", "integer").put("minimum", min).also { if (max != null) it.put("maximum", max) }
    private fun tool(name: String, desc: String, props: JSONObject, vararg req: String) = JSONObject()
        .put("name", name).put("description", desc)
        .put("input_schema", JSONObject().put("type", "object").put("properties", props).also { if (req.isNotEmpty()) it.put("required", JSONArray(req.toList())) })

    private fun browser() = en("default", "firefox", "chrome", "edge", "opera", "brave", "samsung")

    private fun tools(): JSONArray {
        val t = JSONArray()
        t.put(tool("open_url", "Ouvre une adresse web dans le navigateur.", JSONObject().put("url", str("Adresse complète")).put("browser", browser()), "url"))
        t.put(tool("search_in_browser", "Lance une recherche dans le navigateur (Google par défaut, ou YouTube, Wikipédia, Amazon, Google Maps, Google Images).",
            JSONObject().put("query", str("Ce qu'il faut chercher")).put("site", en("google", "youtube", "wikipedia", "amazon", "maps", "images")).put("browser", browser()), "query"))
        t.put(tool("open_app", "Ouvre une application installée (par son nom).", JSONObject().put("name", str("Nom de l'appli")), "name"))
        t.put(tool("set_volume", "Règle le volume des médias (0 à 100) ou coupe le son.", JSONObject().put("level", int(0, 100)).put("mute", JSONObject().put("type", "boolean"))))
        t.put(tool("media", "Commande la musique ou la vidéo en cours.", JSONObject().put("action", en("play_pause", "next", "previous")), "action"))
        t.put(tool("set_brightness", "Règle la luminosité de l'écran (0 à 100).", JSONObject().put("level", int(0, 100)), "level"))
        t.put(tool("flashlight", "Allume ou éteint la lampe torche.", JSONObject().put("on", JSONObject().put("type", "boolean")), "on"))
        t.put(tool("open_panel", "Ouvre un panneau des réglages : wifi, bluetooth, internet, nfc, volume, son, batterie, affichage, reglages.",
            JSONObject().put("panel", en("wifi", "bluetooth", "internet", "nfc", "volume", "son", "batterie", "affichage", "reglages")), "panel"))
        t.put(tool("set_timer", "Lance un minuteur.", JSONObject().put("seconds", int(1)).put("label", str("Nom (facultatif)")), "seconds"))
        t.put(tool("set_alarm", "Programme une alarme.", JSONObject().put("hour", int(0, 23)).put("minute", int(0, 59)).put("label", str("Nom (facultatif)")), "hour", "minute"))
        t.put(tool("set_reminder", "Crée un rappel : dans N minutes, à une heure précise (HH:MM), ou à la prochaine ouverture du PC. Partagé avec le PC lié.",
            JSONObject().put("text", str("Ce qu'il faut rappeler")).put("minutes", int(1)).put("time", str("Heure HH:MM (facultatif)")).put("when_pc_opens", JSONObject().put("type", "boolean")), "text"))
        t.put(tool("ring_pc", "Fait sonner le PC lié (pour le retrouver).", JSONObject()))
        t.put(tool("find_contact", "Cherche un contact par son nom et renvoie ses numéros.", JSONObject().put("name", str("Nom ou surnom")), "name"))
        t.put(tool("call", "Appelle un numéro de téléphone.", JSONObject().put("number", str("Numéro")), "number"))
        t.put(tool("send_sms", "Envoie un SMS. Seulement après que l'utilisateur a confirmé le texte exact.", JSONObject().put("number", str("Numéro")).put("text", str("Texte du message")), "number", "text"))
        return t
    }

    /** Conversation avec Gemini, outils compris (jusqu'a 6 allers-retours). Hors du fil principal. */
    private fun run(q: String): String {
        val contents = JSONArray()
        history.forEach {
            contents.put(JSONObject().put("role", if (it.optString("role") == "assistant") "model" else "user")
                .put("parts", JSONArray().put(JSONObject().put("text", it.optString("content")))))
        }
        contents.put(JSONObject().put("role", "user").put("parts", JSONArray().put(JSONObject().put("text", q))))
        val text = StringBuilder()
        var round = 0
        while (round++ < 7) {
            val decls = JSONArray()
            val all = tools()
            for (i in 0 until all.length()) {
                val t = all.getJSONObject(i)
                decls.put(JSONObject().put("name", t.getString("name")).put("description", t.getString("description")).put("parameters", t.getJSONObject("input_schema")))
            }
            val toolList = JSONArray().put(JSONObject().put("function_declarations", decls))
            if (!noWebSearch) toolList.put(JSONObject().put("google_search", JSONObject()))
            val body = JSONObject()
                .put("system_instruction", JSONObject().put("parts", JSONArray().put(JSONObject().put("text", systemPrompt()))))
                .put("contents", contents)
                .put("tools", toolList)
                .put("generationConfig", JSONObject().put("maxOutputTokens", 900))
            val res = post(body) ?: continue
            val parts = res.optJSONArray("candidates")?.optJSONObject(0)?.optJSONObject("content")?.optJSONArray("parts") ?: JSONArray()
            val replies = JSONArray()
            text.setLength(0)
            for (i in 0 until parts.length()) {
                val p = parts.getJSONObject(i)
                if (p.has("text") && !p.optBoolean("thought")) text.append(p.optString("text"))
                val call = p.optJSONObject("functionCall") ?: continue
                val name = call.optString("name")
                val input = call.optJSONObject("args") ?: JSONObject()
                val out = try { runTool(name, input) } catch (e: Exception) { "Erreur : ${e.message}" }
                Log.i(TAG, "Assistant : outil $name $input -> $out")
                replies.put(JSONObject().put("functionResponse", JSONObject().put("name", name).put("response", JSONObject().put("result", out))))
            }
            if (replies.length() > 0) {
                contents.put(JSONObject().put("role", "model").put("parts", parts))
                contents.put(JSONObject().put("role", "user").put("parts", replies))
                continue
            }
            break
        }
        return text.toString()
    }

    private fun post(body: JSONObject): JSONObject? {
        val c = URL("https://generativelanguage.googleapis.com/v1beta/models/${model()}:generateContent").openConnection() as HttpURLConnection
        try {
            c.requestMethod = "POST"
            c.connectTimeout = 15000
            c.readTimeout = 60000
            c.doOutput = true
            c.setRequestProperty("content-type", "application/json")
            c.setRequestProperty("x-goog-api-key", Prefs.aiKey(app).trim())
            c.outputStream.use { it.write(body.toString().toByteArray()) }
            val code = c.responseCode
            val stream = if (code in 200..299) c.inputStream else c.errorStream
            val json = stream?.bufferedReader()?.use { it.readText() } ?: ""
            if (code in 200..299) return JSONObject(json)
            val msg = try { JSONObject(json).optJSONObject("error")?.optString("message") ?: "" } catch (e: Exception) { "" }
            Log.i(TAG, "Assistant : API $code $msg")
            if (code == 400 && !noWebSearch && (msg.contains("search", true) || msg.contains("tool", true))) { noWebSearch = true; return null }
            throw Exception(when {
                msg.contains("API key", true) -> "La clé Gemini n'est pas valide. Vérifie-la dans Wave Island."
                code == 403 -> "La clé Gemini n'a pas accès à ce modèle."
                code == 404 -> "Modèle Gemini introuvable : choisis-en un autre dans Wave Island."
                code == 429 -> "Limite gratuite de Gemini atteinte pour le moment, réessaie un peu plus tard."
                code == 500 || code == 503 -> "Gemini est surchargé, réessaie dans un instant."
                else -> "Gemini ne répond pas ($code)."
            })
        } catch (e: java.io.IOException) {
            throw Exception("Pas de connexion à Internet.")
        } finally { c.disconnect() }
    }

    private fun clean(s: String): String = s
        .replace(Regex("\\[([^\\]]+)\\]\\([^)]+\\)"), "$1")
        .replace(Regex("https?://\\S+"), "")
        .replace("**", "").replace("__", "").replace("`", "").replace("#", "")
        .replace(Regex("(?m)^\\s*[-*•]\\s+"), "")
        .replace(Regex("\\s+"), " ").trim()

    // ================== Outils ==================

    private val browsers = mapOf(
        "firefox" to "org.mozilla.firefox", "chrome" to "com.android.chrome", "edge" to "com.microsoft.emmx",
        "opera" to "com.opera.browser", "brave" to "com.brave.browser", "samsung" to "com.sec.android.app.sbrowser"
    )

    private fun start(i: Intent): Boolean = try {
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        app.startActivity(i); true
    } catch (e: Exception) { Log.i(TAG, "Assistant : lancement impossible $e"); false }

    private fun installed(pkg: String) = try { app.packageManager.getPackageInfo(pkg, 0); true } catch (e: Exception) { false }

    private fun openUrl(url: String, browser: String?): String {
        val u = if (url.contains(":")) url else "https://$url"
        val i = Intent(Intent.ACTION_VIEW, Uri.parse(u))
        val pkg = browsers[browser ?: ""]
        if (pkg != null) {
            if (installed(pkg)) { i.setPackage(pkg); return if (start(i)) "Ouvert dans $browser." else "Impossible d'ouvrir." }
            return if (start(i)) "$browser n'est pas installé : ouvert dans le navigateur par défaut." else "Impossible d'ouvrir."
        }
        return if (start(i)) "Ouvert." else "Impossible d'ouvrir."
    }

    private fun fold(s: String) = Normalizer.normalize(s, Normalizer.Form.NFD).replace(Regex("\\p{M}"), "").lowercase(Locale.ROOT).trim()

    @SuppressLint("MissingPermission")
    private fun runTool(name: String, a: JSONObject): String {
        fun s(k: String) = if (a.has(k)) a.optString(k) else null
        when (name) {
            "open_url" -> return openUrl(s("url") ?: "", s("browser"))
            "search_in_browser" -> {
                val q = Uri.encode(s("query") ?: "")
                val url = when (s("site")) {
                    "youtube" -> "https://www.youtube.com/results?search_query=$q"
                    "wikipedia" -> "https://fr.wikipedia.org/w/index.php?search=$q"
                    "amazon" -> "https://www.amazon.fr/s?k=$q"
                    "maps" -> "https://www.google.com/maps/search/$q"
                    "images" -> "https://www.google.com/search?tbm=isch&q=$q"
                    else -> "https://www.google.com/search?q=$q"
                }
                return openUrl(url, s("browser"))
            }
            "open_app" -> {
                val q = fold(s("name") ?: "")
                val pm = app.packageManager
                val apps = pm.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0)
                    .map { it.activityInfo.packageName to it.loadLabel(pm).toString() }.distinctBy { it.first }
                val hit = apps.firstOrNull { fold(it.second) == q }
                    ?: apps.filter { fold(it.second).startsWith(q) }.minByOrNull { it.second.length }
                    ?: apps.filter { fold(it.second).contains(q) }.minByOrNull { it.second.length }
                    ?: apps.filter { it.second.length > 2 && q.contains(fold(it.second)) }.maxByOrNull { it.second.length }
                    ?: return "Aucune appli de ce nom n'est installée."
                val i = pm.getLaunchIntentForPackage(hit.first) ?: return "Cette appli ne s'ouvre pas."
                return if (start(i)) "Appli ouverte : ${hit.second}" else "Impossible de l'ouvrir."
            }
            "set_volume" -> {
                val am = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager
                if (a.has("mute")) am.adjustStreamVolume(AudioManager.STREAM_MUSIC, if (a.optBoolean("mute")) AudioManager.ADJUST_MUTE else AudioManager.ADJUST_UNMUTE, 0)
                if (a.has("level")) {
                    val max = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
                    am.setStreamVolume(AudioManager.STREAM_MUSIC, (a.optInt("level") * max + 50) / 100, AudioManager.FLAG_SHOW_UI)
                }
                return "Volume réglé."
            }
            "media" -> {
                val am = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager
                val key = when (s("action")) { "next" -> KeyEvent.KEYCODE_MEDIA_NEXT; "previous" -> KeyEvent.KEYCODE_MEDIA_PREVIOUS; else -> KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE }
                am.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, key))
                am.dispatchMediaKeyEvent(KeyEvent(KeyEvent.ACTION_UP, key))
                return "Fait."
            }
            "set_brightness" -> {
                if (!Settings.System.canWrite(app)) {
                    start(Intent(Settings.ACTION_MANAGE_WRITE_SETTINGS, Uri.parse("package:" + app.packageName)))
                    return "Wave Island n'a pas encore le droit de modifier la luminosité : j'ai ouvert l'autorisation."
                }
                val lvl = a.optInt("level", 50).coerceIn(0, 100)
                Settings.System.putInt(app.contentResolver, Settings.System.SCREEN_BRIGHTNESS_MODE, Settings.System.SCREEN_BRIGHTNESS_MODE_MANUAL)
                Settings.System.putInt(app.contentResolver, Settings.System.SCREEN_BRIGHTNESS, (lvl * 255 / 100).coerceAtLeast(1))
                return "Luminosité réglée."
            }
            "flashlight" -> {
                val cm = app.getSystemService(Context.CAMERA_SERVICE) as CameraManager
                val id = cm.cameraIdList.firstOrNull { cm.getCameraCharacteristics(it).get(android.hardware.camera2.CameraCharacteristics.FLASH_INFO_AVAILABLE) == true }
                    ?: return "Pas de lampe sur ce téléphone."
                cm.setTorchMode(id, a.optBoolean("on", true))
                return if (a.optBoolean("on", true)) "Lampe allumée." else "Lampe éteinte."
            }
            "open_panel" -> {
                val action = when (s("panel")) {
                    "wifi" -> if (Build.VERSION.SDK_INT >= 29) Settings.Panel.ACTION_WIFI else Settings.ACTION_WIFI_SETTINGS
                    "internet" -> if (Build.VERSION.SDK_INT >= 29) Settings.Panel.ACTION_INTERNET_CONNECTIVITY else Settings.ACTION_WIRELESS_SETTINGS
                    "nfc" -> if (Build.VERSION.SDK_INT >= 29) Settings.Panel.ACTION_NFC else Settings.ACTION_NFC_SETTINGS
                    "volume" -> if (Build.VERSION.SDK_INT >= 29) Settings.Panel.ACTION_VOLUME else Settings.ACTION_SOUND_SETTINGS
                    "bluetooth" -> Settings.ACTION_BLUETOOTH_SETTINGS
                    "son" -> Settings.ACTION_SOUND_SETTINGS
                    "batterie" -> Intent.ACTION_POWER_USAGE_SUMMARY
                    "affichage" -> Settings.ACTION_DISPLAY_SETTINGS
                    else -> Settings.ACTION_SETTINGS
                }
                return if (start(Intent(action))) "Panneau ouvert (Android ne laisse pas les applis changer ce réglage toutes seules)." else "Impossible."
            }
            "set_timer" -> {
                val i = Intent(AlarmClock.ACTION_SET_TIMER)
                    .putExtra(AlarmClock.EXTRA_LENGTH, a.optInt("seconds", 60))
                    .putExtra(AlarmClock.EXTRA_SKIP_UI, true)
                s("label")?.takeIf { it.isNotBlank() }?.let { i.putExtra(AlarmClock.EXTRA_MESSAGE, it) }
                return if (start(i)) "Minuteur lancé." else "Aucune appli Horloge pour le minuteur."
            }
            "set_alarm" -> {
                val i = Intent(AlarmClock.ACTION_SET_ALARM)
                    .putExtra(AlarmClock.EXTRA_HOUR, a.optInt("hour")).putExtra(AlarmClock.EXTRA_MINUTES, a.optInt("minute"))
                    .putExtra(AlarmClock.EXTRA_SKIP_UI, true)
                s("label")?.takeIf { it.isNotBlank() }?.let { i.putExtra(AlarmClock.EXTRA_MESSAGE, it) }
                return if (start(i)) "Alarme programmée." else "Aucune appli Horloge pour l'alarme."
            }
            "set_reminder" -> {
                val txt = s("text") ?: ""
                if (a.optBoolean("when_pc_opens")) return if (com.liquidisland.drop.WaveLink.reminderOnPc(txt)) "Rappel à la prochaine ouverture du PC." else "Le PC n'est pas joignable."
                val cal = java.util.Calendar.getInstance()
                when {
                    a.has("minutes") -> cal.add(java.util.Calendar.MINUTE, a.optInt("minutes"))
                    s("time")?.matches(Regex("\\d{1,2}[:h]\\d{2}")) == true -> {
                        val p = s("time")!!.split(':', 'h')
                        cal.set(java.util.Calendar.HOUR_OF_DAY, p[0].toInt()); cal.set(java.util.Calendar.MINUTE, p[1].toInt()); cal.set(java.util.Calendar.SECOND, 0)
                        if (cal.timeInMillis < System.currentTimeMillis()) cal.add(java.util.Calendar.DAY_OF_YEAR, 1)
                    }
                    else -> cal.add(java.util.Calendar.HOUR_OF_DAY, 1)
                }
                com.liquidisland.drop.WaveLink.addReminder(txt, cal.timeInMillis)
                return "Rappel programmé pour " + java.text.SimpleDateFormat("EEEE HH:mm", Locale.FRANCE).format(cal.time) + "."
            }
            "ring_pc" -> {
                val n = com.liquidisland.drop.WaveLink.sendAll(com.liquidisland.drop.WaveLink.msg("ring"))
                return if (n > 0) "Le PC sonne." else "Le PC n'est pas joignable."
            }
            "find_contact" -> {
                if (app.checkSelfPermission(Manifest.permission.READ_CONTACTS) != PackageManager.PERMISSION_GRANTED)
                    return "Wave Island n'a pas accès aux contacts (à autoriser dans l'appli Wave Island, section Assistant IA)."
                val q = fold(s("name") ?: "")
                val out = ArrayList<String>()
                app.contentResolver.query(ContactsContract.CommonDataKinds.Phone.CONTENT_URI,
                    arrayOf(ContactsContract.CommonDataKinds.Phone.DISPLAY_NAME, ContactsContract.CommonDataKinds.Phone.NUMBER), null, null, null)?.use { cur ->
                    while (cur.moveToNext() && out.size < 8) {
                        val n = cur.getString(0) ?: continue
                        val num = cur.getString(1) ?: continue
                        val f = fold(n)
                        if (f.contains(q) || q.contains(f)) out.add("$n : $num")
                    }
                }
                return if (out.isEmpty()) "Aucun contact trouvé pour « ${s("name")} »." else out.distinct().joinToString("\n")
            }
            "call" -> {
                val num = s("number") ?: return "Pas de numéro."
                val canCall = app.checkSelfPermission(Manifest.permission.CALL_PHONE) == PackageManager.PERMISSION_GRANTED
                val i = Intent(if (canCall) Intent.ACTION_CALL else Intent.ACTION_DIAL, Uri.parse("tel:" + Uri.encode(num)))
                return if (start(i)) (if (canCall) "Appel lancé." else "Numéro composé, touche Appeler.") else "Impossible d'appeler."
            }
            "send_sms" -> {
                val num = s("number") ?: return "Pas de numéro."
                val txt = s("text") ?: return "Pas de texte."
                if (app.checkSelfPermission(Manifest.permission.SEND_SMS) != PackageManager.PERMISSION_GRANTED) {
                    start(Intent(Intent.ACTION_SENDTO, Uri.parse("smsto:" + Uri.encode(num))).putExtra("sms_body", txt))
                    return "Message prêt dans l'appli SMS : touche Envoyer."
                }
                @Suppress("DEPRECATION")
                val sm = if (Build.VERSION.SDK_INT >= 31) app.getSystemService(SmsManager::class.java) else SmsManager.getDefault()
                sm.sendMultipartTextMessage(num, null, sm.divideMessage(txt), null, null)
                return "SMS envoyé."
            }
        }
        return "Outil inconnu."
    }

    // ================== Voix ==================

    private fun speak(text: String, done: () -> Unit) {
        val ready = { t: TextToSpeech ->
            t.language = Locale.FRANCE
            t.setSpeechRate(1.05f)
            t.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
                override fun onStart(id: String?) {}
                override fun onDone(id: String?) { main.post(done) }
                @Deprecated("Deprecated in Java") override fun onError(id: String?) { main.post(done) }
                override fun onStop(id: String?, interrupted: Boolean) { main.post(done) }
            })
            t.speak(text, TextToSpeech.QUEUE_FLUSH, Bundle(), "wave-" + SystemClock.uptimeMillis())
        }
        val t = tts
        if (t != null && ttsReady) { ready(t); return }
        tts = TextToSpeech(app) { st ->
            ttsReady = st == TextToSpeech.SUCCESS
            val e = tts
            if (ttsReady && e != null) ready(e) else main.post(done)
        }
    }

    private fun stopSpeaking() { try { tts?.stop() } catch (e: Exception) { } }

    // Pour les tests automatiques : l'ilot dans chaque etat, sans micro ni reseau
    fun demo(ctx: Context, step: Int) {
        app = ctx.applicationContext
        when (step) {
            0 -> { heard = ""; state = State.LISTENING; show("Je t'écoute…") }
            1 -> { heard = "Cherche la météo de Paris sur Firefox"; state = State.THINKING; show("Je réfléchis…") }
            2 -> { state = State.DONE; show("", "J'ai ouvert la recherche « météo Paris » dans Firefox. Il fait 12 degrés avec quelques nuages cet après-midi.", done = true) }
        }
    }
}
