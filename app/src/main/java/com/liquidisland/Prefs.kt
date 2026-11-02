package com.liquidisland

import android.content.Context

object Prefs {
    private fun sp(c: Context) = c.getSharedPreferences("island", Context.MODE_PRIVATE)

    fun hideSystem(c: Context) = sp(c).getBoolean("hide", false)
    fun setHideSystem(c: Context, v: Boolean) = sp(c).edit().putBoolean("hide", v).apply()

    fun blur(c: Context) = sp(c).getBoolean("blur", true)
    fun setBlur(c: Context, v: Boolean) = sp(c).edit().putBoolean("blur", v).apply()

    /** true = l'ilot doit tourner (et redemarrer au demarrage du telephone) */
    fun enabled(c: Context) = sp(c).getBoolean("enabled", false)
    fun setEnabled(c: Context, v: Boolean) = sp(c).edit().putBoolean("enabled", v).apply()
}
