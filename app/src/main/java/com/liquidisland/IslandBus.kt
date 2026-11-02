package com.liquidisland

import android.app.PendingIntent
import android.graphics.drawable.Drawable

data class IslandNote(
    val title: String,
    val text: String,
    val icon: Drawable?,
    val intent: PendingIntent?
)

/** Petit pont entre l'ecoute des notifications et la fenetre de l'ilot. */
object IslandBus {
    @Volatile
    var listener: ((IslandNote) -> Unit)? = null

    fun post(note: IslandNote) {
        listener?.invoke(note)
    }
}
