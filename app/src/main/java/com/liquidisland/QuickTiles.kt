package com.liquidisland

import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.liquidisland.drop.WaveDrop

/** Pastille « Wave Drop » des reglages rapides : active / coupe Wave Drop (appui long : l'appli). */
class WaveDropTile : TileService() {
    override fun onStartListening() = refresh()

    override fun onClick() {
        WaveDrop.init(this)
        val on = !WaveDrop.enabled(this)
        if (on && !WaveDrop.hasPermissions(this)) {
            // Il faut d'abord autoriser le Bluetooth : on ouvre l'onglet Wave Drop
            openApp(Intent(this, MainActivity::class.java).putExtra("ci", "drop"))
            return
        }
        WaveDrop.setEnabled(this, on)
        if (on) {
            WaveDrop.restart(this)
            if (IslandService.instance == null && Prefs.enabled(this)) try { startForegroundService(Intent(this, IslandService::class.java)) } catch (e: Exception) { }
        } else WaveDrop.stop()
        refresh()
    }

    private fun refresh() {
        val t = qsTile ?: return
        val on = WaveDrop.enabled(this)
        t.state = if (on) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        t.label = "Wave Drop"
        if (Build.VERSION.SDK_INT >= 29) t.subtitle = if (on) "Visible" else "Désactivé"
        t.icon = Icon.createWithResource(this, R.drawable.ic_tile_drop)
        t.updateTile()
    }

    private fun openApp(i: Intent) {
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(android.app.PendingIntent.getActivity(this, 0, i,
                android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(i)
        }
    }
}

/** Pastille « Wave Island » : affiche / cache l'ilot. */
class IslandTile : TileService() {
    override fun onStartListening() = refresh()

    override fun onClick() {
        val on = !Prefs.enabled(this)
        Prefs.setEnabled(this, on)
        if (on) {
            try { startForegroundService(Intent(this, IslandService::class.java)) } catch (e: Exception) { }
        } else stopService(Intent(this, IslandService::class.java))
        refresh()
    }

    private fun refresh() {
        val t = qsTile ?: return
        val on = Prefs.enabled(this)
        t.state = if (on) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        t.label = "Wave Island"
        if (Build.VERSION.SDK_INT >= 29) t.subtitle = if (on) "Affichée" else "Cachée"
        t.icon = Icon.createWithResource(this, R.drawable.ic_tile_island)
        t.updateTile()
    }
}
