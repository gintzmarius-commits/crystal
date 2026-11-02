package com.liquidisland

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/** Verifie sur GitHub s'il existe une version plus recente, la telecharge et lance l'installation. */
object Updater {
    private const val BASE = "https://github.com/gintzmarius-commits/crystal/releases/latest/download/"
    private const val TAG = "LiquidIsland"

    sealed class State {
        object UpToDate : State()
        data class Available(val version: Int) : State()
        data class Downloading(val percent: Int) : State()
        object NeedsPermission : State()
        object Installing : State()
        data class Error(val message: String) : State()
    }

    @Volatile private var busy = false

    private fun open(url: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10000
            readTimeout = 20000
            instanceFollowRedirects = true
            setRequestProperty("Cache-Control", "no-cache")
        }

    fun check(ctx: Context, onState: (State) -> Unit) {
        if (busy) return
        busy = true
        val app = ctx.applicationContext
        val ui = Handler(Looper.getMainLooper())
        fun post(s: State) = ui.post { onState(s) }
        Thread {
            try {
                val remote = open(BASE + "version.txt").inputStream.bufferedReader().readText().trim().toInt()
                Log.i(TAG, "Version installee ${BuildConfig.VERSION_CODE}, en ligne $remote")
                if (remote <= BuildConfig.VERSION_CODE) {
                    post(State.UpToDate); return@Thread
                }
                post(State.Available(remote))
                if (!app.packageManager.canRequestPackageInstalls()) {
                    post(State.NeedsPermission); return@Thread
                }
                val file = File(app.cacheDir, "update.apk")
                val c = open(BASE + "WaveIsland.apk")
                val total = c.contentLengthLong
                c.inputStream.use { input ->
                    file.outputStream().use { out ->
                        val buf = ByteArray(64 * 1024)
                        var done = 0L
                        var last = -1
                        while (true) {
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            done += n
                            val p = if (total > 0) (done * 100 / total).toInt() else 0
                            if (p != last) { last = p; post(State.Downloading(p)) }
                        }
                    }
                }
                install(app, file)
                post(State.Installing)
            } catch (e: Exception) {
                Log.w(TAG, "Mise a jour impossible", e)
                post(State.Error(e.message ?: e.javaClass.simpleName))
            } finally {
                busy = false
            }
        }.start()
    }

    private fun install(ctx: Context, apk: File) {
        val installer = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        if (Build.VERSION.SDK_INT >= 31) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val id = installer.createSession(params)
        installer.openSession(id).use { session ->
            session.openWrite("base.apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            val pi = PendingIntent.getBroadcast(ctx, id, Intent(ctx, InstallReceiver::class.java), flags)
            session.commit(pi.intentSender)
        }
    }
}

/** Recoit le resultat de l'installation ; ouvre la confirmation d'Android si elle est demandee. */
class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, -999)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT) ?: return
                confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                try { context.startActivity(confirm) } catch (e: Exception) { Log.w("LiquidIsland", "Confirmation impossible", e) }
            }
            PackageInstaller.STATUS_SUCCESS -> Log.i("LiquidIsland", "Mise a jour installee")
            else -> Log.w("LiquidIsland", "Installation : " + intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE))
        }
    }
}
