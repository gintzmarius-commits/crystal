package com.liquidisland

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.drawable.Drawable
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.AudioManager
import android.os.Build
import android.provider.MediaStore
import android.provider.Settings
import android.util.Log

/**
 * Raccourcis du mini menu (appui long sur l'ilot).
 * Un raccourci est soit une action « action:xxx », soit une appli « app:paquet ».
 */
object Shortcuts {
    const val MAX = 8

    data class Item(val id: String, val label: String, val icon: Drawable?, val tintIcon: Boolean)

    /** Actions integrees : identifiant, nom, icone */
    val ACTIONS = listOf(
        Triple("action:torch", "Lampe", R.drawable.sc_torch),
        Triple("action:camera", "Photo", R.drawable.sc_camera),
        Triple("action:wifi", "Wi-Fi", R.drawable.sc_wifi),
        Triple("action:bluetooth", "Bluetooth", R.drawable.sc_bluetooth),
        Triple("action:volume", "Volume", R.drawable.sc_volume),
        Triple("action:settings", "Réglages", R.drawable.sc_settings),
        Triple("action:wave", "Wave Island", R.drawable.sc_wave)
    )

    private val DEFAULT = listOf("action:torch", "action:camera", "action:wifi", "action:volume")

    private fun sp(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE)

    fun load(c: Context): List<String> {
        val raw = sp(c).getString("shortcuts", null) ?: return DEFAULT
        return raw.split("|").filter { it.isNotBlank() }
    }

    fun save(c: Context, list: List<String>) =
        sp(c).edit().putString("shortcuts", list.take(MAX).joinToString("|")).apply()

    fun resolve(c: Context, id: String): Item? {
        ACTIONS.firstOrNull { it.first == id }?.let {
            return Item(id, it.second, c.getDrawable(it.third), true)
        }
        if (id.startsWith("app:")) {
            val pkg = id.removePrefix("app:")
            return try {
                val info = c.packageManager.getApplicationInfo(pkg, 0)
                Item(id, c.packageManager.getApplicationLabel(info).toString(), c.packageManager.getApplicationIcon(info), false)
            } catch (e: PackageManager.NameNotFoundException) {
                null // appli desinstallee
            }
        }
        return null
    }

    /** Applis installees qu'on peut lancer, triees par nom. */
    fun launchableApps(c: Context): List<Pair<String, String>> {
        val pm = c.packageManager
        val i = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        @Suppress("DEPRECATION")
        return pm.queryIntentActivities(i, 0)
            .map { "app:" + it.activityInfo.packageName to it.loadLabel(pm).toString() }
            .filter { it.first != "app:" + c.packageName }
            .distinctBy { it.first }
            .sortedBy { it.second.lowercase() }
    }

    private var torchOn = false
    private var torchCallback: CameraManager.TorchCallback? = null

    /** Lance le raccourci. Renvoie false si rien n'a pu etre ouvert. */
    fun run(c: Context, id: String): Boolean {
        Log.i("LiquidIsland", "Raccourci : $id")
        return try {
            when (id) {
                "action:torch" -> { toggleTorch(c); true }
                "action:volume" -> {
                    (c.getSystemService(Context.AUDIO_SERVICE) as AudioManager)
                        .adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_SAME, AudioManager.FLAG_SHOW_UI)
                    true
                }
                "action:camera" -> start(c, Intent(MediaStore.INTENT_ACTION_STILL_IMAGE_CAMERA))
                "action:wifi" -> start(
                    c,
                    if (Build.VERSION.SDK_INT >= 29) Intent(Settings.Panel.ACTION_WIFI) else Intent(Settings.ACTION_WIFI_SETTINGS)
                )
                "action:bluetooth" -> start(c, Intent(Settings.ACTION_BLUETOOTH_SETTINGS))
                "action:settings" -> start(c, Intent(Settings.ACTION_SETTINGS))
                "action:wave" -> start(c, Intent(c, MainActivity::class.java))
                else -> {
                    val pkg = id.removePrefix("app:")
                    val launch = c.packageManager.getLaunchIntentForPackage(pkg) ?: return false
                    start(c, launch)
                }
            }
        } catch (e: Exception) {
            Log.w("LiquidIsland", "Raccourci impossible : $id", e)
            false
        }
    }

    private fun start(c: Context, i: Intent): Boolean {
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        c.startActivity(i)
        return true
    }

    private fun toggleTorch(c: Context) {
        val cm = c.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val id = cm.cameraIdList.firstOrNull {
            cm.getCameraCharacteristics(it).get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true
        } ?: return
        if (torchCallback == null) {
            // Suit l'etat reel de la lampe (si elle est coupee ailleurs)
            torchCallback = object : CameraManager.TorchCallback() {
                override fun onTorchModeChanged(cameraId: String, enabled: Boolean) {
                    if (cameraId == id) torchOn = enabled
                }
            }
            cm.registerTorchCallback(torchCallback!!, android.os.Handler(android.os.Looper.getMainLooper()))
        }
        torchOn = !torchOn
        cm.setTorchMode(id, torchOn)
    }
}
