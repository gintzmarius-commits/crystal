package com.liquidisland

import android.app.Activity
import android.content.Intent
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.util.Log

/**
 * Ecran invisible qui demande a Android l'autorisation de capture de l'ecran (« Commencer maintenant »),
 * puis la transmet a l'ilot pour l'effet goutte d'eau en direct.
 */
class CaptureConsentActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val mpm = getSystemService(MediaProjectionManager::class.java)
        val intent = if (Build.VERSION.SDK_INT >= 34) {
            // Tout l'ecran (pas une seule appli) : l'ilot doit voir ce qui est derriere lui partout
            mpm.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
        } else {
            mpm.createScreenCaptureIntent()
        }
        try {
            startActivityForResult(intent, REQ)
        } catch (e: Exception) {
            Log.w("LiquidIsland", "Demande de capture impossible : $e")
            finish()
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ && resultCode == RESULT_OK && data != null) {
            Log.i("LiquidIsland", "Capture d'ecran autorisee")
            LiveCapture.pendingResult = resultCode to data
            Prefs.setEnabled(this, true)
            startForegroundService(Intent(this, IslandService::class.java).setAction(IslandService.ACTION_LIVE))
        } else {
            Log.i("LiquidIsland", "Capture d'ecran refusee")
        }
        finish()
        overridePendingTransition(0, 0)
    }

    companion object {
        private const val REQ = 42
    }
}
