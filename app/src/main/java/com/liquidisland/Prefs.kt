package com.liquidisland

import android.content.Context

object Prefs {
    private fun sp(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE)

    fun hideSystem(c: Context) = sp(c).getBoolean("hide", false)
    fun setHideSystem(c: Context, v: Boolean) = sp(c).edit().putBoolean("hide", v).apply()

    /** true = l'ilot doit tourner (et redemarrer au demarrage du telephone) */
    fun enabled(c: Context) = sp(c).getBoolean("enabled", false)
    fun setEnabled(c: Context, v: Boolean) = sp(c).edit().putBoolean("enabled", v).apply()

    /** Forme : false = ile (capsule flottante), true = encoche (collee en haut) */
    fun notch(c: Context) = sp(c).getBoolean("notch", false)
    fun setNotch(c: Context, v: Boolean) = sp(c).edit().putBoolean("notch", v).apply()

    /** Teinte du verre : "clear" (transparent, par defaut), "dark" ou "light" */
    fun theme(c: Context) = sp(c).getString("theme2", "clear") ?: "clear"
    fun setTheme(c: Context, v: String) = sp(c).edit().putString("theme2", v).apply()

    /** En plein ecran (video, jeu) : "show" = l'ilot reste contre la camera, "hide" = cache */
    fun fullscreen(c: Context) = sp(c).getString("fullscreen2", "show") ?: "show"
    fun setFullscreen(c: Context, v: String) = sp(c).edit().putString("fullscreen2", v).apply()

    /** Flou du fond, limite a la forme de l'ilot */
    /** Plus de flou : l'ilot est toujours en verre transparent */
    fun blur(c: Context) = false
    fun setBlur(c: Context, v: Boolean) = sp(c).edit().putBoolean("blur3", v).apply()

    /** Deformation du fond sur les bords (capture de l'ecran, Android 13+) */
    fun refraction(c: Context) = sp(c).getBoolean("refraction", true)
    fun setRefraction(c: Context, v: Boolean) = sp(c).edit().putBoolean("refraction", v).apply()

    /** L'utilisateur veut l'effet en direct (a accepte la capture) : on la repropose si Android la coupe */
    fun liveWanted(c: Context) = sp(c).getBoolean("liveWanted", false)
    fun setLiveWanted(c: Context, v: Boolean) = sp(c).edit().putBoolean("liveWanted", v).apply()

    fun isDark(c: Context): Boolean = theme(c) != "light"
    fun isClear(c: Context): Boolean = theme(c) == "clear"

    /** Indicateur de volume facon iPhone (remplace celui du telephone) */
    fun volumeHud(c: Context) = sp(c).getBoolean("volumeHud", true)
    fun setVolumeHud(c: Context, v: Boolean) = sp(c).edit().putBoolean("volumeHud", v).apply()

    /** Cote des boutons de volume : "right" (Xiaomi, Samsung...) ou "left" */
    fun volumeSide(c: Context) = sp(c).getString("volumeSide", "right") ?: "right"
    fun setVolumeSide(c: Context, v: String) = sp(c).edit().putString("volumeSide", v).apply()

    /** Hauteur de l'indicateur, en % de la hauteur de l'ecran (la ou sont les boutons) */
    fun volumePos(c: Context) = sp(c).getInt("volumePos", 30)
    fun setVolumePos(c: Context, v: Int) = sp(c).edit().putInt("volumePos", v).apply()

    /** Musique en cours dans l'ilot (pochette, commandes) */
    fun liveMedia(c: Context) = sp(c).getBoolean("liveMedia", true)
    fun setLiveMedia(c: Context, v: Boolean) = sp(c).edit().putBoolean("liveMedia", v).apply()

    /** Telechargements et autres taches en cours (barre de progression) dans l'ilot */
    fun liveDownloads(c: Context) = sp(c).getBoolean("liveDownloads", true)
    fun setLiveDownloads(c: Context, v: Boolean) = sp(c).edit().putBoolean("liveDownloads", v).apply()

    // Tailles en dp
    const val W_MIN = 60; const val W_MAX = 260
    const val H_MIN = 20; const val H_MAX = 60
    const val Y_MIN = 0; const val Y_MAX = 40

    fun width(c: Context) = sp(c).getInt("w", 120)
    fun setWidth(c: Context, v: Int) = sp(c).edit().putInt("w", v).apply()

    fun height(c: Context) = sp(c).getInt("h", 34)
    fun setHeight(c: Context, v: Int) = sp(c).edit().putInt("h", v).apply()

    fun offset(c: Context) = sp(c).getInt("y", 8)
    fun setOffset(c: Context, v: Int) = sp(c).edit().putInt("y", v).apply()

    /** Centre de notifications (glisser depuis le haut a gauche) et de controle (a droite) facon Wave Island */
    fun shade(c: Context) = sp(c).getBoolean("shade", true)
    fun setShade(c: Context, v: Boolean) = sp(c).edit().putBoolean("shade", v).apply()

    /** Wave Theme : dock facon iPad (toucher la barre tout en bas de l'ecran) */
    fun dock(c: Context) = sp(c).getBoolean("dock", true)
    fun setDock(c: Context, v: Boolean) = sp(c).edit().putBoolean("dock", v).apply()
    fun dockApps(c: Context): List<String> = (sp(c).getString("dockApps", "") ?: "").split("|").filter { it.isNotBlank() }
    fun setDockApps(c: Context, l: List<String>) = sp(c).edit().putString("dockApps", l.joinToString("|")).apply()
    fun dockHeight(c: Context) = sp(c).getInt("dockH", 104)
    fun setDockHeight(c: Context, v: Int) = sp(c).edit().putInt("dockH", v).apply()
    fun dockBottom(c: Context) = sp(c).getInt("dockY", 16)
    fun setDockBottom(c: Context, v: Int) = sp(c).edit().putInt("dockY", v).apply()

    // ----- Version finale : assistant IA (Gemini) -----
    fun aiKey(c: Context) = sp(c).getString("aiKey", "") ?: ""
    fun setAiKey(c: Context, v: String) = sp(c).edit().putString("aiKey", v.trim()).apply()
    fun aiModel(c: Context) = sp(c).getString("aiModel", "gemini-flash-latest") ?: "gemini-flash-latest"
    fun setAiModel(c: Context, v: String) = sp(c).edit().putString("aiModel", v).apply()
    fun aiVoice(c: Context) = sp(c).getBoolean("aiVoice", true)
    fun setAiVoice(c: Context, v: Boolean) = sp(c).edit().putBoolean("aiVoice", v).apply()
}
