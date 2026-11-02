package com.liquidisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.provider.Settings

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        if (!Prefs.enabled(context) || !Settings.canDrawOverlays(context)) return
        try {
            context.startForegroundService(Intent(context, IslandService::class.java))
        } catch (e: Exception) {
            // Certains constructeurs bloquent le demarrage : l'utilisateur doit autoriser le demarrage automatique
        }
    }
}
