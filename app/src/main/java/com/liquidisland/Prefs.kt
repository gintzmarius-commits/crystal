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

    /** En plein ecran (video, jeu) : "hide" = cacher l'ilot, "camera" = le reduire autour de la camera */
    fun fullscreen(c: Context) = sp(c).getString("fullscreen", "hide") ?: "hide"
    fun setFullscreen(c: Context, v: String) = sp(c).edit().putString("fullscreen", v).apply()

    /** Flou du fond, limite a la forme de l'ilot */
    /** Plus de flou : l'ilot est toujours en verre transparent */
    fun blur(c: Context) = false
    fun setBlur(c: Context, v: Boolean) = sp(c).edit().putBoolean("blur3", v).apply()

    /** Deformation du fond sur les bords (capture de l'ecran, Android 13+) */
    fun refraction(c: Context) = sp(c).getBoolean("refraction", true)
    fun setRefraction(c: Context, v: Boolean) = sp(c).edit().putBoolean("refraction", v).apply()

    fun isDark(c: Context): Boolean = theme(c) != "light"
    fun isClear(c: Context): Boolean = theme(c) == "clear"

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
}
