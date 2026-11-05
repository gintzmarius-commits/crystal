package com.liquidisland.assist

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.speech.RecognitionListener
import android.speech.RecognitionService
import android.speech.SpeechRecognizer
import android.service.voice.VoiceInteractionService
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.util.Log

/**
 * Wave Island comme assistant du telephone (a la place de Gemini) : un appui long sur le bouton
 * d'alimentation (ou le geste de l'assistant) reveille la Wave Island qui ecoute.
 * Reglages > Applis > Applis par defaut > Assistant numerique : Wave Island.
 */
class WaveVoiceService : VoiceInteractionService() {
    override fun onReady() {
        super.onReady()
        Log.i("LiquidIsland", "Assistant : Wave Island est l'assistant du telephone")
    }
}

class WaveSessionService : VoiceInteractionSessionService() {
    override fun onNewSession(args: Bundle?): VoiceInteractionSession = WaveSession(this)
}

class WaveSession(ctx: Context) : VoiceInteractionSession(ctx) {
    override fun onShow(args: Bundle?, showFlags: Int) {
        super.onShow(args, showFlags)
        Log.i("LiquidIsland", "Assistant : appui long (assistant du telephone)")
        // Pas de fenetre a nous : c'est l'ilot qui s'active, puis la session se referme
        val island = com.liquidisland.IslandService.instance
        if (island != null) island.startAssist()
        else {
            // Ilot pas lance : l'ecoute marche quand meme (fenetre transparente) et l'ilot sera relance
            Assistant.trigger(context)
        }
        hide()
    }
}

/**
 * Service de reconnaissance vocale exige par Android pour un assistant : il passe simplement la main
 * a la vraie reconnaissance du telephone (Google), pour que la dictee des autres applis marche toujours.
 */
class WaveRecognitionService : RecognitionService() {
    private var sr: SpeechRecognizer? = null

    override fun onStartListening(intent: Intent, cb: Callback) {
        sr?.destroy()
        val comp: ComponentName? = AssistActivity.recognizer(this)
        val r = try { if (comp != null) SpeechRecognizer.createSpeechRecognizer(this, comp) else null } catch (e: Exception) { null }
        if (r == null) { try { cb.error(SpeechRecognizer.ERROR_CLIENT) } catch (e: Exception) { }; return }
        sr = r
        r.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(p: Bundle?) { tryIt { cb.readyForSpeech(p ?: Bundle()) } }
            override fun onBeginningOfSpeech() { tryIt { cb.beginningOfSpeech() } }
            override fun onRmsChanged(v: Float) { tryIt { cb.rmsChanged(v) } }
            override fun onBufferReceived(b: ByteArray?) { tryIt { if (b != null) cb.bufferReceived(b) } }
            override fun onEndOfSpeech() { tryIt { cb.endOfSpeech() } }
            override fun onError(e: Int) { tryIt { cb.error(e) } }
            override fun onResults(b: Bundle?) { tryIt { cb.results(b ?: Bundle()) } }
            override fun onPartialResults(b: Bundle?) { tryIt { cb.partialResults(b ?: Bundle()) } }
            override fun onEvent(t: Int, p: Bundle?) {}
        })
        r.startListening(intent)
    }

    private inline fun tryIt(f: () -> Unit) { try { f() } catch (e: Exception) { } }

    override fun onStopListening(cb: Callback) { sr?.stopListening() }
    override fun onCancel(cb: Callback) { sr?.cancel() }
    override fun onDestroy() { sr?.destroy(); sr = null; super.onDestroy() }
}

/** Ancien raccourci « Assistant » (ACTION_ASSIST / commande vocale) : meme chose. */
class AssistEntryActivity : android.app.Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val island = com.liquidisland.IslandService.instance
        if (island != null) island.startAssist() else Assistant.trigger(this)
        finish()
        if (Build.VERSION.SDK_INT >= 34) overrideActivityTransition(OVERRIDE_TRANSITION_CLOSE, 0, 0)
        else @Suppress("DEPRECATION") overridePendingTransition(0, 0)
    }
}
