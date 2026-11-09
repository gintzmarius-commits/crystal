package com.liquidisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Debug
import android.util.Log

/**
 * Mesures de performance (tests automatiques) : ecrit dans le journal les compteurs de memoire de l'appli
 * (octets alloues, nombre et duree des ramasse-miettes). Ne fait rien d'autre.
 */
class PerfReceiver : BroadcastReceiver() {
    override fun onReceive(c: Context, i: Intent) {
        val alloc = Debug.getRuntimeStat("art.gc.bytes-allocated") ?: "?"
        val gcs = Debug.getRuntimeStat("art.gc.gc-count") ?: "?"
        val gcTime = Debug.getRuntimeStat("art.gc.gc-time") ?: "?"
        val rt = Runtime.getRuntime()
        Log.i("WavePerf", "alloc=$alloc gc=$gcs gcms=$gcTime heap=${(rt.totalMemory() - rt.freeMemory()) / 1024}")
    }
}
