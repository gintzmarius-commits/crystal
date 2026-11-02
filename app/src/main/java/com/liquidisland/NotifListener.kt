package com.liquidisland

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

class NotifListener : NotificationListenerService() {

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName == packageName) return
        val n = sbn.notification ?: return

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

        IslandBus.post(IslandNote(title, text, icon, n.contentIntent))

        // Option : supprime la notification systeme pour que seul l'ilot l'affiche
        if (Prefs.hideSystem(this)) {
            try {
                cancelNotification(sbn.key)
            } catch (e: Exception) {
                // ignore
            }
        }
    }
}
