package com.liquidisland

import android.app.Application
import android.content.Context
import android.os.Build
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Enregistre les plantages pour pouvoir les envoyer depuis l'appli. */
class WaveApp : Application() {
    override fun onCreate() {
        super.onCreate()
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { t, e ->
            try {
                val date = SimpleDateFormat("dd/MM HH:mm:ss", Locale.FRANCE).format(Date())
                File(filesDir, CRASH_FILE).writeText(
                    "Plantage le $date (fil ${t.name})\n" + android.util.Log.getStackTraceString(e)
                )
            } catch (x: Exception) { }
            previous?.uncaughtException(t, e)
        }
    }

    companion object {
        const val CRASH_FILE = "dernier-plantage.txt"

        fun crash(c: Context): String? =
            File(c.filesDir, CRASH_FILE).takeIf { it.exists() }?.readText()

        fun clearCrash(c: Context) { File(c.filesDir, CRASH_FILE).delete() }

        /** Rapport complet : appareil, version, dernier plantage et journal recent de l'appli. */
        fun report(c: Context): String {
            val sb = StringBuilder()
            sb.append("Wave Island ${BuildConfig.VERSION_NAME}\n")
            sb.append("${Build.MANUFACTURER} ${Build.MODEL}, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})\n")
            sb.append("Accessibilite connectee : ${IslandAccessibility.instance != null}\n")
            sb.append("Ilot actif : ${IslandService.instance != null}, effet en direct : ${IslandService.instance?.isLive == true}\n\n")
            crash(c)?.let { sb.append(it).append("\n\n") }
            try {
                val p = Runtime.getRuntime().exec(arrayOf("logcat", "-d", "-v", "time", "-t", "500", "--pid=${android.os.Process.myPid()}"))
                sb.append("--- Journal ---\n")
                sb.append(p.inputStream.bufferedReader().readText())
            } catch (e: Exception) {
                sb.append("Journal indisponible : $e")
            }
            return sb.toString()
        }
    }
}
