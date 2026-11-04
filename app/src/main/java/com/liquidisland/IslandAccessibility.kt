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
            if (t == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) event.packageName?.toString()?.let { IslandService.instance?.onForegroundApp(it) }
            // Les changements de contenu sont tres frequents : ils ne servent qu'a reperer le volet
            if (t != AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED) IslandService.instance?.onScreenChanged()
        } catch (e: Exception) { Log.w("LiquidIsland", "Evenement : $e") }
    }
    override fun onInterrupt() {}

    private var dockOn = false
    private var dockStrip = 0

    /**
     * Dock Wave Theme : la bande tout en bas de l'ecran passe par l'accessibilite (avant Android),
     * pour qu'un glissement vers le haut ouvre le dock au lieu de quitter l'appli. Tout le reste de
     * l'ecran est laisse tel quel (zones « passthrough »).
     */
    fun setDockGestures(on: Boolean, stripPx: Int) {
        if (android.os.Build.VERSION.SDK_INT < 30) return
        if (on == dockOn && (!on || stripPx == dockStrip)) return
        try {
            val wm = getSystemService(WINDOW_SERVICE) as android.view.WindowManager
            val b = wm.currentWindowMetrics.bounds
            if (on) {
                val keep = android.graphics.Region(0, 0, b.width(), b.height() - stripPx)
                setTouchExplorationPassthroughRegion(android.view.Display.DEFAULT_DISPLAY, keep)
                setGestureDetectionPassthroughRegion(android.view.Display.DEFAULT_DISPLAY, keep)
            }
            val info = serviceInfo
            val f = android.accessibilityservice.AccessibilityServiceInfo.FLAG_REQUEST_TOUCH_EXPLORATION_MODE
            info.flags = if (on) info.flags or f else info.flags and f.inv()
            serviceInfo = info
            dockOn = on
            dockStrip = stripPx
            Log.i("LiquidIsland", "Dock : geste du bas ${if (on) "pris par Wave Island ($stripPx px)" else "rendu a Android"}")
        } catch (e: Exception) { Log.w("LiquidIsland", "Dock : geste du bas impossible : $e") }
    }

    override fun onGesture(gestureEvent: android.accessibilityservice.AccessibilityGestureEvent): Boolean {
        val id = gestureEvent.gestureId
        Log.i("LiquidIsland", "Dock : geste $id")
        when (id) {
            GESTURE_SWIPE_UP, GESTURE_SWIPE_UP_AND_DOWN, GESTURE_2_FINGER_SWIPE_UP -> IslandService.instance?.dockSwipeUp()
            GESTURE_SWIPE_LEFT, GESTURE_SWIPE_RIGHT, GESTURE_SWIPE_UP_AND_LEFT, GESTURE_SWIPE_UP_AND_RIGHT -> IslandService.instance?.dockSwipeSide()
        }
        return true
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
        @Volatile
        var instance: IslandAccessibility? = null
            private set
    }
}
