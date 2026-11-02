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
