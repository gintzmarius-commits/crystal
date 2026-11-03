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
        Log.i("LiquidIsland", "Accessibilite active")
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
        IslandService.instance?.onScreenChanged()
    }
    override fun onInterrupt() {}

    /** Boutons de volume : l'indicateur facon iPhone remplace celui du telephone. */
    override fun onKeyEvent(event: android.view.KeyEvent): Boolean =
        IslandService.instance?.onVolumeKey(event) ?: false

    companion object {
        @Volatile
        var instance: IslandAccessibility? = null
            private set
    }
}
