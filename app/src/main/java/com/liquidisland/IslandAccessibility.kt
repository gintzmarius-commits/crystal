package com.liquidisland

import android.accessibilityservice.AccessibilityService
import android.content.Intent
import android.util.Log
import android.view.accessibility.AccessibilityEvent

/**
 * Service d'accessibilite : il ne lit rien a l'ecran. Il sert uniquement a placer l'ilot
 * AU-DESSUS de la barre d'etat, sinon Android donne les touchers a la barre d'etat
 * (et glisser vers le bas ouvrirait le volet des notifications).
 */
class IslandAccessibility : AccessibilityService() {

    override fun onServiceConnected() {
        instance = this
        val filter = (serviceInfo?.flags ?: 0) and android.accessibilityservice.AccessibilityServiceInfo.FLAG_REQUEST_FILTER_KEY_EVENTS != 0
        Log.i("LiquidIsland", "Accessibilite active (filtre des touches : ${if (filter) "oui" else "non"})")
        val island = IslandService.instance
        if (island != null) {
            island.rehost()
        } else if (Prefs.enabled(this)) {
            try {
                startForegroundService(Intent(this, IslandService::class.java))
            } catch (e: Exception) {
                Log.w("LiquidIsland", "Demarrage de l'ilot refuse", e)
            }
        }
    }

    override fun onUnbind(intent: Intent?): Boolean {
        if (instance === this) instance = null
        IslandService.instance?.rehost()
        return super.onUnbind(intent)
    }

    /** L'ecran change (appli ouverte, defilement) : l'ilot cache son bord deforme le temps d'une nouvelle capture. */
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // Jamais de plantage ici : Android couperait l'accessibilite (l'ilot passerait sous la barre d'etat)
        try {
            val t = event?.eventType ?: return
            // Volet des notifications / centre de controle (HyperOS : miui.systemui.plugin)
            // (un panneau qui APPARAIT, pas la mise a jour de l'heure ou du debit dans la barre d'etat)
            val pane = t == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED ||
                (t == AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED && android.os.Build.VERSION.SDK_INT >= 28 &&
                    event.contentChangeTypes and AccessibilityEvent.CONTENT_CHANGE_TYPE_PANE_APPEARED != 0)
            if (pane &&
                event.packageName?.toString()?.contains("systemui") == true) IslandService.instance?.onShadeShown()
            if (t == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) event.packageName?.toString()?.let {
                IslandService.instance?.onForegroundApp(it)
                // Lot 4 (continuite) : on quitte le navigateur -> la page part vers le PC
                if (it != lastBrowser && lastBrowser != null && it !in URL_BARS && !it.contains("systemui")) { com.liquidisland.drop.WaveLink.leftBrowser(); lastBrowser = null }
            }
            // Lot 4 (continuite) : adresse de la page du navigateur (au plus toutes les 3 s)
            val pkg = event.packageName?.toString()
            if (pkg != null && pkg in URL_BARS && android.os.SystemClock.uptimeMillis() - lastUrlRead > 3000) {
                lastUrlRead = android.os.SystemClock.uptimeMillis()
                lastBrowser = pkg
                readUrl(pkg)
            }
            // Les changements de contenu sont tres frequents : ils ne servent qu'a reperer le volet
            if (t != AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED) IslandService.instance?.onScreenChanged()
        } catch (e: Exception) { Log.w("LiquidIsland", "Evenement : $e") }
    }
    override fun onInterrupt() {}

    private var lastUrlRead = 0L
    private var lastBrowser: String? = null

    private fun readUrl(pkg: String) {
        val root = rootInActiveWindow ?: return
        try {
            val node = root.findAccessibilityNodeInfosByViewId(URL_BARS[pkg]).firstOrNull() ?: return
            val text = node.text?.toString()?.trim() ?: return
            if (text.isEmpty() || text.contains(' ') || !text.contains('.')) return
            com.liquidisland.drop.WaveLink.browserPage(text, null)
        } catch (e: Exception) { }
    }

    /** Boutons de volume : l'indicateur facon iPhone remplace celui du telephone. */
    override fun onKeyEvent(event: android.view.KeyEvent): Boolean = try {
        if (event.action == android.view.KeyEvent.ACTION_DOWN && event.repeatCount == 0) {
            Log.i("LiquidIsland", "Touche recue : ${android.view.KeyEvent.keyCodeToString(event.keyCode)}")
        }
        IslandService.instance?.onVolumeKey(event) ?: false
    } catch (e: Exception) {
        Log.w("LiquidIsland", "Touche : $e")
        false
    }

    companion object {
        /** Barre d'adresse des navigateurs (continuite). */
        val URL_BARS = mapOf(
            "com.android.chrome" to "com.android.chrome:id/url_bar",
            "org.mozilla.firefox" to "org.mozilla.firefox:id/mozac_browser_toolbar_url_view",
            "com.microsoft.emmx" to "com.microsoft.emmx:id/url_bar",
            "com.brave.browser" to "com.brave.browser:id/url_bar",
            "com.opera.browser" to "com.opera.browser:id/url_field",
            "com.sec.android.app.sbrowser" to "com.sec.android.app.sbrowser:id/location_bar_edit_text",
            "com.mi.globalbrowser" to "com.mi.globalbrowser:id/url"
        )
        @Volatile
        var instance: IslandAccessibility? = null
            private set
    }
}
