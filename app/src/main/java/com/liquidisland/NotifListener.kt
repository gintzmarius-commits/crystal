package com.liquidisland

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log

class NotifListener : NotificationListenerService() {

    override fun onListenerConnected() {
        instance = this
        Log.i("LiquidIsland", "Acces aux notifications actif")
        IslandService.instance?.onListenerReady()
        replayTasks()
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        changed()
        IslandService.instance?.taskGone(sbn.key)
    }

    /**
     * Notification avec une barre de progression (telechargement, envoi, installation...) :
     * elle devient une activite en cours dans l'ilot. Renvoie true si c'en est une.
     */
    private fun asTask(sbn: StatusBarNotification): Boolean {
        val n = sbn.notification ?: return false
        val ex = n.extras
        val max = ex.getInt(Notification.EXTRA_PROGRESS_MAX, 0)
        val indet = ex.getBoolean(Notification.EXTRA_PROGRESS_INDETERMINATE, false)
        if (max <= 0 && !indet) {
            // Plus de barre (tache terminee) : on la retire de l'ilot
            IslandService.instance?.taskGone(sbn.key)
            return false
        }
        // Les lecteurs de musique ont parfois une barre : la musique est geree a part
        if (n.category == Notification.CATEGORY_TRANSPORT || ex.containsKey(Notification.EXTRA_MEDIA_SESSION)) return false
        val title = (ex.getCharSequence(Notification.EXTRA_TITLE) ?: ex.getCharSequence(Notification.EXTRA_TITLE_BIG))?.toString().orEmpty()
        val text = (ex.getCharSequence(Notification.EXTRA_TEXT) ?: ex.getCharSequence(Notification.EXTRA_SUB_TEXT))?.toString().orEmpty()
        val icon = try { packageManager.getApplicationIcon(sbn.packageName) } catch (e: Exception) { null }
        val label = try {
            packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
        } catch (e: Exception) { sbn.packageName }
        IslandService.instance?.task(
            ProgressNow(
                sbn.key, sbn.packageName, title.ifBlank { label }, text.ifBlank { label },
                ex.getInt(Notification.EXTRA_PROGRESS, 0), max, indet, icon, n.contentIntent
            )
        )
        return true
    }

    override fun onListenerDisconnected() {
        if (instance === this) instance = null
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        changed()
        if (sbn.packageName == packageName) return
        val n = sbn.notification ?: return
        if (asTask(sbn)) return

        // On ignore les resumes de groupe et les notifications permanentes (musique, appels, services...)
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return
        if (sbn.isOngoing || n.flags and Notification.FLAG_FOREGROUND_SERVICE != 0) return

        val ex = n.extras
        val title = ex.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = (ex.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: ex.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty()
        if (title.isEmpty() && text.isEmpty()) return

        val icon = try {
            packageManager.getApplicationIcon(sbn.packageName)
        } catch (e: Exception) {
            null
        }
        val autoCancel = n.flags and Notification.FLAG_AUTO_CANCEL != 0

        IslandService.deliver(
            this,
            IslandNote(title, text, icon, n.contentIntent, sbn.packageName, sbn.key, autoCancel),
            sbn.packageName
        )

        // Option : supprime la notification systeme pour que seul l'ilot l'affiche
        if (Prefs.hideSystem(this)) {
            // ... mais on la garde dans le centre de notifications de Wave Island
            kept.removeAll { it.key == sbn.key }
            kept.add(0, Kept(sbn.key, sbn.packageName, title, text, sbn.postTime, n.contentIntent, autoCancel))
            while (kept.size > 60) kept.removeAt(kept.size - 1)
            try {
                cancelNotification(sbn.key)
            } catch (e: Exception) {
                // ignore
            }
        }
    }

    /** Notification retiree du volet d'Android (option « seulement sur l'ilot ») mais gardee pour notre centre. */
    data class Kept(val key: String, val pkg: String, val title: String, val text: String, val time: Long,
                    val intent: android.app.PendingIntent?, val autoCancel: Boolean)

    companion object {
        @Volatile
        var instance: NotifListener? = null
            private set

        val kept = java.util.concurrent.CopyOnWriteArrayList<Kept>()
        /** Appeles (sur le fil principal) quand les notifications changent. */
        val listeners = java.util.concurrent.CopyOnWriteArrayList<() -> Unit>()
        private val ui = android.os.Handler(android.os.Looper.getMainLooper())
        private val notify = Runnable { listeners.forEach { try { it() } catch (e: Exception) { } } }
        fun changed() { ui.removeCallbacks(notify); ui.postDelayed(notify, 120) }

        /** Renvoie a l'ilot les taches deja en cours (au demarrage de l'ilot). */
        fun replayTasks() {
            val l = instance ?: return
            try { l.activeNotifications?.forEach { if (it.packageName != l.packageName) l.asTask(it) } } catch (e: Exception) { }
        }

        /** Retire une notification du volet (apres l'avoir ouverte depuis l'ilot). */
        fun cancel(key: String) {
            try {
                instance?.cancelNotification(key)
            } catch (e: Exception) {
                // ignore
            }
        }
    }
}
