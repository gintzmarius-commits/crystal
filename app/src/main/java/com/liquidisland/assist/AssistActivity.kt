package com.liquidisland.assist

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.speech.RecognitionListener
import android.speech.RecognitionService
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.util.Log
import android.view.MotionEvent
import android.view.WindowManager

/**
 * Petite fenetre transparente le temps de l'ecoute (Android ne donne le micro qu'a une appli visible).
 * Toucher l'ecran en dehors de l'ilot annule. L'ilot affiche tout (ce qui est entendu en direct).
 */
class AssistActivity : Activity() {

    companion object {
        private const val TAG = "LiquidIsland"
        var current: AssistActivity? = null
            private set

        /** Le vrai service de reconnaissance (Google en priorite), jamais celui de Wave Island. */
        fun recognizer(c: Context): ComponentName? {
            val list = c.packageManager.queryIntentServices(Intent(RecognitionService.SERVICE_INTERFACE), 0)
                .map { ComponentName(it.serviceInfo.packageName, it.serviceInfo.name) }
                .filter { it.packageName != c.packageName }
            return list.firstOrNull { it.packageName == "com.google.android.googlequicksearchbox" }
                ?: list.firstOrNull { it.packageName.startsWith("com.google") }
                ?: list.firstOrNull()
        }
    }

    private var sr: SpeechRecognizer? = null
    private var finished = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = this
        window.addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        overridePendingTransition(0, 0)
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 7)
        } else begin()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) begin()
        else { Assistant.failed("Autorise le micro pour Wave Island (Réglages > Applis > Wave Island > Autorisations)."); finishQuiet() }
    }

    private fun begin() {
        val comp = recognizer(this)
        val r = try {
            if (comp != null) SpeechRecognizer.createSpeechRecognizer(this, comp) else SpeechRecognizer.createSpeechRecognizer(this)
        } catch (e: Exception) { null }
        if (r == null) { Assistant.failed("Aucune reconnaissance vocale sur ce téléphone (installe l'appli Google)."); finishQuiet(); return }
        sr = r
        r.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(params: Bundle?) {}
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(rmsdB: Float) {}
            override fun onBufferReceived(buffer: ByteArray?) {}
            override fun onEndOfSpeech() {}
            override fun onEvent(eventType: Int, params: Bundle?) {}
            override fun onPartialResults(b: Bundle?) {
                b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.let { Assistant.partial(it) }
            }
            override fun onResults(b: Bundle?) {
                val t = b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull() ?: ""
                Assistant.heardDone(t)
                finishQuiet()
            }
            override fun onError(error: Int) {
                Log.i(TAG, "Assistant : ecoute erreur $error")
                when (error) {
                    SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT -> Assistant.heardDone("")
                    SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> Assistant.failed("Autorise le micro pour Wave Island.")
                    SpeechRecognizer.ERROR_NETWORK, SpeechRecognizer.ERROR_NETWORK_TIMEOUT -> Assistant.failed("Pas de connexion pour la reconnaissance vocale.")
                    else -> Assistant.failed("La reconnaissance vocale n'a pas marché (erreur $error).")
                }
                finishQuiet()
            }
        })
        val i = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
            .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            .putExtra(RecognizerIntent.EXTRA_LANGUAGE, "fr-FR")
            .putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            .putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE, packageName)
        try { r.startListening(i) } catch (e: Exception) { Assistant.failed("Micro indisponible : ${e.message}"); finishQuiet() }
    }

    /** Toucher l'ilot pendant l'ecoute : on envoie tout de suite ce qui a ete dit. */
    fun stopListening() { try { sr?.stopListening() } catch (e: Exception) { } }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        // Toucher ailleurs que l'ilot : on annule
        if (event.actionMasked == MotionEvent.ACTION_UP) { Assistant.close(); com.liquidisland.IslandService.instance?.closeAssist() }
        return true
    }

    fun finishQuiet() {
        if (finished) return
        finished = true
        try { sr?.cancel(); sr?.destroy() } catch (e: Exception) { }
        sr = null
        if (current === this) current = null
        finish()
        overridePendingTransition(0, 0)
    }

    override fun onDestroy() {
        if (!finished) { finished = true; try { sr?.destroy() } catch (e: Exception) { } }
        if (current === this) current = null
        super.onDestroy()
    }
}
