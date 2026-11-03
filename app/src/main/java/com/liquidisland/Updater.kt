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

    /** Derniere version vue en ligne (0 = inconnue). */
    @Volatile var latest = 0
        private set

    fun apkFile(ctx: Context) = File(ctx.applicationContext.cacheDir, "update.apk")

    /** Regarde seulement s'il existe une version plus recente (sans telecharger). */
    fun peek(ctx: Context, onResult: (Int?) -> Unit) {
        val ui = Handler(Looper.getMainLooper())
        Thread {
            val r = try {
                val remote = open(BASE + "version.txt?t=" + System.currentTimeMillis()).inputStream.bufferedReader().readText().trim().toInt()
                latest = remote
                if (remote > BuildConfig.VERSION_CODE) remote else null
            } catch (e: Exception) {
                Log.w(TAG, "Verification de mise a jour impossible : $e")
                null
            }
            ui.post { onResult(r) }
        }.start()
    }

    private fun open(url: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10000
            readTimeout = 20000
            instanceFollowRedirects = true
            useCaches = false
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
                val remote = open(BASE + "version.txt?t=" + System.currentTimeMillis()).inputStream.bufferedReader().readText().trim().toInt()
                latest = remote
                Log.i(TAG, "Version installee ${BuildConfig.VERSION_CODE}, en ligne $remote")
                if (remote <= BuildConfig.VERSION_CODE) {
                    post(State.UpToDate); return@Thread
                }
                post(State.Available(remote))
                if (!app.packageManager.canRequestPackageInstalls()) {
                    post(State.NeedsPermission); return@Thread
                }
                val file = apkFile(app)
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
                Log.w(TAG, "Mise a jour impossible : $e")
                post(State.Error(e.message ?: e.javaClass.simpleName))
            } finally {
                busy = false
            }
        }.start()
    }

    /**
     * Installation par l'installateur d'Android (ecran « Installer ? ») : marche aussi quand
     * HyperOS / One UI refusent l'installation directe.
     */
    fun installWithSystem(ctx: Context) {
        val f = apkFile(ctx)
        if (!f.exists()) return
        val uri = android.net.Uri.parse("content://com.liquidisland.apk/update.apk")
        @Suppress("DEPRECATION")
        val i = Intent(Intent.ACTION_INSTALL_PACKAGE).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            putExtra(Intent.EXTRA_NOT_UNKNOWN_SOURCE, true)
        }
        try {
            ctx.startActivity(i)
            Log.i(TAG, "Installation par l'installateur d'Android")
        } catch (e: Exception) {
            Log.w(TAG, "Installateur d'Android indisponible", e)
        }
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
            else -> {
                // Installation directe refusee (certains Xiaomi / Samsung) : on passe par l'installateur d'Android
                Log.w("LiquidIsland", "Installation : " + intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE))
                Updater.installWithSystem(context)
            }
        }
    }
}

/** Fournit update.apk a l'installateur d'Android (lecture seule, aucun autre fichier). */
class ApkProvider : android.content.ContentProvider() {
    override fun onCreate() = true
    override fun getType(uri: android.net.Uri) = "application/vnd.android.package-archive"
    override fun openFile(uri: android.net.Uri, mode: String): android.os.ParcelFileDescriptor? {
        val f = Updater.apkFile(context ?: return null)
        return android.os.ParcelFileDescriptor.open(f, android.os.ParcelFileDescriptor.MODE_READ_ONLY)
    }
    override fun query(uri: android.net.Uri, p: Array<String>?, s: String?, a: Array<String>?, o: String?): android.database.Cursor? {
        val f = Updater.apkFile(context ?: return null)
        val c = android.database.MatrixCursor(arrayOf(android.provider.OpenableColumns.DISPLAY_NAME, android.provider.OpenableColumns.SIZE))
        c.addRow(arrayOf<Any>("WaveIsland.apk", f.length()))
        return c
    }
    override fun insert(uri: android.net.Uri, v: android.content.ContentValues?): android.net.Uri? = null
    override fun delete(uri: android.net.Uri, s: String?, a: Array<String>?) = 0
    override fun update(uri: android.net.Uri, v: android.content.ContentValues?, s: String?, a: Array<String>?) = 0
}
