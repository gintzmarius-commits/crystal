package com.liquidisland.drop

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.random.Random

/**
 * Wave Drop sur Android : diffuse la balise en Bluetooth, ecoute les connexions (Wi-Fi local et Bluetooth L2CAP),
 * detecte les appareils proches (Bluetooth et NFC), envoie et recoit les fichiers.
 * Le transport est choisi tout seul : Wi-Fi s'il est commun (rapide), sinon Bluetooth.
 */
@SuppressLint("MissingPermission")
object WaveDrop {
    private const val TAG = "LiquidIsland"
    /** Signal a partir duquel l'appareil est considere « colle » (cote a cote). */
    const val CLOSE_RSSI = -55

    class Nearby(
        @Volatile var beacon: WaveDropProtocol.Beacon,
        @Volatile var rssi: Float,
        @Volatile var lastSeen: Long,
        @Volatile var name: String? = null,
        @Volatile var device: BluetoothDevice? = null,
        @Volatile var viaNfc: Boolean = false
    ) {
        val label get() = name ?: if (beacon.isPc) "PC Windows" else "Téléphone Android"
        val close get() = viaNfc || rssi >= CLOSE_RSSI
    }

    class Outgoing(val uri: Uri, val name: String, val size: Long, val mime: String)

    /** Demande recue, en attente de la reponse de l'utilisateur. */
    class Request(val id: Int, val from: String, val offer: WaveDropProtocol.Offer) {
        private val latch = CountDownLatch(1)
        @Volatile var accepted = false
            private set
        fun answer(ok: Boolean) { accepted = ok; latch.countDown() }
        fun await(): Boolean = latch.await(90, TimeUnit.SECONDS) && accepted
    }

    /** Tout ce qui doit etre affiche (ilot, panneau, onglet) passe par ici. */
    interface Ui {
        fun nearbyChanged() {}
        fun request(r: Request) {}
        fun progress(title: String, text: String, done: Long, total: Long) {}
        fun finished(title: String, text: String, ok: Boolean, received: Uri? = null, mime: String? = null) {}
    }

    private lateinit var app: Context
    private val main = Handler(Looper.getMainLooper())
    val uis = CopyOnWriteArrayList<Ui>()
    val nearby = ConcurrentHashMap<Int, Nearby>()
    val requests = ConcurrentHashMap<Int, Request>()

    @Volatile var running = false
        private set
    @Volatile var outgoing: Outgoing? = null
    /** Appareil a qui on destine le fichier (pour qu'un PC sans Wi-Fi commun vienne le chercher : pas utilise cote telephone). */
    @Volatile var fastScan = false
        set(v) { field = v; if (running) restartScan() }

    private var server: ServerSocket? = null
    private var l2cap: BluetoothServerSocket? = null
    private var psm = 0
    private var adv: AdvertiseCallback? = null
    private var scan: ScanCallback? = null
    private val busy = AtomicBoolean(false)
    private val pulling = ConcurrentHashMap.newKeySet<Int>()
    private val naming = ConcurrentHashMap.newKeySet<Int>()

    fun init(ctx: Context) { if (!::app.isInitialized) app = ctx.applicationContext }

    private fun prefs(c: Context = app) = c.getSharedPreferences("wavedrop", Context.MODE_PRIVATE)

    fun enabled(c: Context) = prefs(c.applicationContext).getBoolean("on", false)
    fun setEnabled(c: Context, v: Boolean) {
        val was = enabled(c)
        prefs(c.applicationContext).edit().putBoolean("on", v).apply()
        if (was != v) android.os.Handler(android.os.Looper.getMainLooper()).post { com.liquidisland.IslandService.instance?.dropToggled(v) }
    }

    val myId: Int get() {
        val p = prefs()
        var id = p.getInt("id", 0)
        if (id == 0) { id = Random.nextInt().let { if (it == 0) 1 else it }; p.edit().putInt("id", id).apply() }
        return id
    }

    var myName: String
        get() = prefs().getString("name", null) ?: defaultName()
        set(v) { prefs().edit().putString("name", v.trim().take(40)).apply() }

    private fun defaultName(): String {
        val n = try { android.provider.Settings.Global.getString(app.contentResolver, "device_name") } catch (e: Exception) { null }
        return n?.takeIf { it.isNotBlank() } ?: "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}"
    }

    /** Permissions Bluetooth necessaires (demandees depuis l'onglet Wave Drop). */
    fun permissions(): Array<String> = if (Build.VERSION.SDK_INT >= 31)
        arrayOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_ADVERTISE, Manifest.permission.BLUETOOTH_CONNECT)
    else arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)

    fun hasPermissions(c: Context) = permissions().all { c.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }

    private val adapter: BluetoothAdapter? get() = (app.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    // ================== Demarrage ==================

    @Synchronized
    fun start(ctx: Context) {
        init(ctx)
        if (running || !enabled(app)) return
        running = true
        Log.i(TAG, "Wave Drop : demarrage (${myName})")
        startServer()
        watchNetwork()
        if (hasPermissions(app) && adapter?.isEnabled == true) {
            startL2cap()
            startAdvertising()
            restartScan()
        } else Log.i(TAG, "Wave Drop : Bluetooth indisponible ou non autorise (Wi-Fi seulement)")
    }

    @Synchronized
    fun stop() {
        if (!running) return
        running = false
        try { server?.close() } catch (e: Exception) { }
        try { l2cap?.close() } catch (e: Exception) { }
        server = null; l2cap = null; psm = 0
        stopAdvertising()
        stopScan()
        nearby.clear()
        Log.i(TAG, "Wave Drop : arret")
    }

    fun restart(ctx: Context) { stop(); start(ctx) }

    private var netCb: ConnectivityManager.NetworkCallback? = null

    /** Changement de Wi-Fi : la balise annonce la nouvelle adresse. */
    private fun watchNetwork() {
        if (netCb != null) return
        val cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: android.net.Network) { main.postDelayed({ networkChanged() }, 800) }
            override fun onLost(network: android.net.Network) { main.postDelayed({ networkChanged() }, 800) }
            override fun onLinkPropertiesChanged(network: android.net.Network, lp: android.net.LinkProperties) { main.postDelayed({ networkChanged() }, 800) }
        }
        try { cm.registerDefaultNetworkCallback(cb); netCb = cb } catch (e: Exception) { }
    }

    // ================== Balise ==================

    /** Adresse IPv4 sur le Wi-Fi (null si pas de Wi-Fi). */
    private fun wifiIp(): ByteArray? {
        val cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        for (n in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(n) ?: continue
            if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && !caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) continue
            val lp = cm.getLinkProperties(n) ?: continue
            for (la in lp.linkAddresses) WaveDropProtocol.ipv4(la.address)?.let { return it }
        }
        return null
    }

    fun beacon() = WaveDropProtocol.Beacon(myId, false, wifiIp(), server?.localPort ?: 0, null, psm, 0)

    private fun startAdvertising() {
        val le = adapter?.bluetoothLeAdvertiser ?: return
        stopAdvertising()
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_BALANCED)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .setConnectable(true)
            .build()
        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .addManufacturerData(WaveDropProtocol.COMPANY_ID, beacon().encode())
            .build()
        val cb = object : AdvertiseCallback() {
            override fun onStartFailure(errorCode: Int) { Log.w(TAG, "Wave Drop : diffusion Bluetooth refusee ($errorCode)") }
            override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) { Log.i(TAG, "Wave Drop : visible en Bluetooth") }
        }
        adv = cb
        try { le.startAdvertising(settings, data, cb) } catch (e: Exception) { Log.w(TAG, "Wave Drop : diffusion impossible", e) }
    }

    private fun stopAdvertising() {
        val cb = adv ?: return
        adv = null
        try { adapter?.bluetoothLeAdvertiser?.stopAdvertising(cb) } catch (e: Exception) { }
    }

    /** L'adresse Wi-Fi a change (autre reseau) : on met la balise a jour. */
    fun networkChanged() { if (running && adv != null) startAdvertising() }

    // ================== Detection des appareils proches ==================

    private fun restartScan() {
        stopScan()
        val sc = adapter?.bluetoothLeScanner ?: return
        val filter = ScanFilter.Builder().setManufacturerData(WaveDropProtocol.COMPANY_ID, byteArrayOf('W'.code.toByte(), 'D'.code.toByte()), byteArrayOf(-1, -1)).build()
        val settings = ScanSettings.Builder()
            .setScanMode(if (fastScan) ScanSettings.SCAN_MODE_LOW_LATENCY else ScanSettings.SCAN_MODE_LOW_POWER)
            .build()
        val cb = object : ScanCallback() {
            override fun onScanResult(callbackType: Int, result: ScanResult) = seen(result)
            override fun onBatchScanResults(results: MutableList<ScanResult>) { results.forEach { seen(it) } }
            override fun onScanFailed(errorCode: Int) { Log.w(TAG, "Wave Drop : recherche Bluetooth impossible ($errorCode)") }
        }
        scan = cb
        try { sc.startScan(listOf(filter), settings, cb) } catch (e: Exception) { Log.w(TAG, "Wave Drop : recherche impossible", e) }
    }

    private fun stopScan() {
        val cb = scan ?: return
        scan = null
        try { adapter?.bluetoothLeScanner?.stopScan(cb) } catch (e: Exception) { }
    }

    private fun seen(r: ScanResult) {
        val b = WaveDropProtocol.Beacon.decode(r.scanRecord?.getManufacturerSpecificData(WaveDropProtocol.COMPANY_ID)) ?: return
        if (b.id == myId) return
        val now = System.currentTimeMillis()
        val n = nearby[b.id]
        if (n == null) {
            nearby[b.id] = Nearby(b, r.rssi.toFloat(), now, device = r.device)
            Log.i(TAG, "Wave Drop : appareil detecte (${if (b.isPc) "PC" else "telephone"}, ${r.rssi} dBm)")
            resolveName(b.id)
        } else {
            n.beacon = b
            n.rssi = n.rssi * 0.6f + r.rssi * 0.4f
            n.lastSeen = now
            n.device = r.device
        }
        // Un PC qui n'a pas de Wi-Fi commun nous propose un fichier : on va le chercher en Bluetooth
        if (b.offerFor == myId && b.isPc && pulling.add(b.id)) Thread { pullFrom(b) }.start()
        notifyNearby()
    }

    /** Telephones colles : le menu Wave Drop sort de l'ilot (s'il n'est pas deja ouvert). */
    fun touched(c: Context) {
        if (uis.any { it is WaveDropActivity }) return
        val island = com.liquidisland.IslandService.instance
        if (island != null) island.openWaveDropFromNfc() else WaveDropActivity.open(c)
    }

    /** Appareil detecte en collant les telephones (NFC). */
    fun nfcPeer(b: WaveDropProtocol.Beacon, name: String) {
        if (b.id == myId) return
        val n = nearby[b.id]
        if (n == null) nearby[b.id] = Nearby(b, -30f, System.currentTimeMillis(), name, viaNfc = true)
        else { n.beacon = b; n.name = name; n.viaNfc = true; n.lastSeen = System.currentTimeMillis() }
        Log.i(TAG, "Wave Drop : appareil colle (NFC) : $name")
        notifyNearby()
    }

    /** Les appareils encore la, les plus proches d'abord. */
    fun nearbyList(): List<Nearby> {
        val now = System.currentTimeMillis()
        nearby.values.removeIf { now - it.lastSeen > (if (it.viaNfc) 60_000 else 12_000) }
        return nearby.values.sortedWith(compareByDescending<Nearby> { it.viaNfc }.thenByDescending { it.rssi })
    }

    private fun notifyNearby() = main.post { uis.forEach { it.nearbyChanged() } }

    /** Le nom d'un appareil : on lui demande par le Wi-Fi (sans rien envoyer). */
    private fun resolveName(id: Int) {
        val n = nearby[id] ?: return
        val ip = n.beacon.ipString ?: return
        if (n.beacon.port == 0 || !naming.add(id)) return
        Thread {
            try {
                Socket().use { s ->
                    s.connect(InetSocketAddress(ip, n.beacon.port), 1500)
                    s.soTimeout = 4000
                    val ses = WaveDropProtocol.Session(s.getInputStream(), s.getOutputStream(), connector = true)
                    ses.handshake(hello(offer = false))
                    n.name = ses.peer["name"]
                    notifyNearby()
                }
            } catch (e: Exception) { Log.i(TAG, "Wave Drop : nom non obtenu par le Wi-Fi ($e)") }
            finally { naming.remove(id) }
        }.start()
    }

    private fun hello(offer: Boolean) = mapOf(
        "name" to myName, "platform" to "android", "id" to myId.toString(), "offer" to if (offer) "1" else "0"
    )

    // ================== Serveurs (reception) ==================

    private fun startServer() {
        try {
            val s = ServerSocket(0)
            server = s
            Thread({
                while (running && !s.isClosed) {
                    try {
                        val c = s.accept()
                        c.soTimeout = 120_000
                        Thread { handle(c.getInputStream(), c.getOutputStream(), connector = false, "Wi-Fi") { c.close() } }.start()
                    } catch (e: Exception) { if (running) Log.w(TAG, "Wave Drop : serveur Wi-Fi", e) }
                }
            }, "wavedrop-wifi").start()
            Log.i(TAG, "Wave Drop : en attente sur le Wi-Fi (port ${s.localPort})")
        } catch (e: Exception) { Log.w(TAG, "Wave Drop : serveur Wi-Fi impossible", e) }
    }

    private fun startL2cap() {
        if (Build.VERSION.SDK_INT < 29) return
        try {
            val s = adapter?.listenUsingInsecureL2capChannel() ?: return
            l2cap = s
            psm = s.psm
            Thread({
                while (running) {
                    try {
                        val c = s.accept()
                        Thread { handle(c.inputStream, c.outputStream, connector = false, "Bluetooth") { c.close() } }.start()
                    } catch (e: Exception) { if (running) Log.w(TAG, "Wave Drop : serveur Bluetooth", e); break }
                }
            }, "wavedrop-bt").start()
            Log.i(TAG, "Wave Drop : en attente en Bluetooth (canal $psm)")
        } catch (e: Exception) { Log.w(TAG, "Wave Drop : Bluetooth L2CAP impossible", e) }
    }

    /** Une connexion entrante (ou sortante pour aller chercher un fichier) : presentation, puis reception si offre. */
    private fun handle(i: InputStream, o: OutputStream, connector: Boolean, via: String, close: () -> Unit) {
        try {
            val s = WaveDropProtocol.Session(i, o, connector)
            s.handshake(hello(offer = false))
            val peerName = s.peer["name"] ?: "Appareil"
            s.peer["id"]?.toIntOrNull()?.let { id -> nearby[id]?.let { if (it.name == null) { it.name = peerName; notifyNearby() } } }
            if (s.peer["offer"] != "1") return // simple demande de nom
            receive(s, peerName, via)
        } catch (e: Exception) {
            Log.w(TAG, "Wave Drop : connexion interrompue ($via) : $e")
        } finally {
            try { close() } catch (e: Exception) { }
        }
    }

    private fun receive(s: WaveDropProtocol.Session, from: String, via: String) {
        var saved: Uri? = null
        var file: File? = null
        var offerMime = "application/octet-stream"
        var title = ""
        var total = -1L
        val offer = try {
            WaveDropProtocol.receiveFile(s,
                ask = { offer ->
                    offerMime = offer.mime
                    title = offer.name
                    total = offer.size
                    val r = Request(Random.nextInt(), from, offer)
                    requests[r.id] = r
                    Log.i(TAG, "Wave Drop : $from veut envoyer ${offer.name} (${offer.size} octets, $via)")
                    main.post { uis.forEach { it.request(r) } }
                    val ok = r.await()
                    requests.remove(r.id)
                    Log.i(TAG, "Wave Drop : demande ${if (ok) "acceptee" else "refusee"}")
                    ok
                },
                sink = { offer ->
                    val (uri, out, f) = openDestination(offer.name, offer.mime)
                    saved = uri; file = f
                    out
                },
                progress = { got -> main.post { uis.forEach { it.progress("Réception de $title", "De $from", got, total) } } }
            )
        } catch (e: Exception) {
            saved?.let { try { app.contentResolver.delete(it, null, null) } catch (x: Exception) { } }
            file?.delete()
            main.post { uis.forEach { it.finished("Réception interrompue", e.message ?: "Erreur", false) } }
            return
        }
        if (offer == null) { main.post { uis.forEach { it.finished("Refusé", title, false) } }; return }
        // Fichier complet : visible dans la galerie / les fichiers
        saved?.let { uri ->
            if (Build.VERSION.SDK_INT >= 29) {
                val v = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
                try { app.contentResolver.update(uri, v, null, null) } catch (e: Exception) { }
            }
        }
        val uri = saved ?: file?.let { Uri.fromFile(it) }
        History.add(app, offer.name, from, uri)
        Log.i(TAG, "Wave Drop : recu ${offer.name}")
        main.post { uis.forEach { it.finished("Reçu de $from", offer.name, true, uri, offerMime) } }
    }

    /** Ecrit dans Telechargements/Wave Drop (visible dans l'appli Fichiers). */
    private fun openDestination(name: String, mime: String): Triple<Uri?, OutputStream, File?> {
        val safe = name.replace(Regex("[\\\\/:*?\"<>|]"), "_").ifBlank { "fichier" }
        if (Build.VERSION.SDK_INT >= 29) {
            val v = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, safe)
                put(MediaStore.MediaColumns.MIME_TYPE, mime)
                put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Wave Drop")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
            val uri = app.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v) ?: error("impossible d'enregistrer")
            return Triple(uri, app.contentResolver.openOutputStream(uri) ?: error("impossible d'ecrire"), null)
        }
        @Suppress("DEPRECATION")
        val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Wave Drop").apply { mkdirs() }
        var f = File(dir, safe)
        var k = 1
        while (f.exists()) f = File(dir, safe.substringBeforeLast('.') + " ($k)" + (if (safe.contains('.')) "." + safe.substringAfterLast('.') else "")).also { k++ }
        return Triple(null, FileOutputStream(f), f)
    }

    // ================== Envoi ==================

    /** Infos d'un fichier choisi (nom, taille, type). */
    fun describe(c: Context, uri: Uri): Outgoing {
        var name = "fichier"
        var size = -1L
        c.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use {
            if (it.moveToFirst()) {
                it.getString(0)?.let { n -> name = n }
                if (!it.isNull(1)) size = it.getLong(1)
            }
        }
        val mime = c.contentResolver.getType(uri) ?: "application/octet-stream"
        return Outgoing(uri, name, size, mime)
    }

    /** Envoie le fichier choisi a cet appareil (Wi-Fi s'il est commun, sinon Bluetooth). */
    fun send(target: Nearby) {
        val out = outgoing ?: return
        if (!busy.compareAndSet(false, true)) return
        Thread {
            var sock: java.io.Closeable? = null
            try {
                val c = connect(target)
                sock = c.fourth
                val via = c.third
                val s = WaveDropProtocol.Session(c.first, c.second, connector = true)
                s.handshake(hello(offer = true))
                val to = s.peer["name"] ?: target.label
                Log.i(TAG, "Wave Drop : envoi de ${out.name} a $to ($via)")
                main.post { uis.forEach { it.progress("En attente de $to", "Il doit accepter ${out.name}", 0, out.size) } }
                val ok = app.contentResolver.openInputStream(out.uri)!!.use { src ->
                    WaveDropProtocol.sendFile(s, WaveDropProtocol.Offer(out.name, out.size, out.mime), src) { sent ->
                        main.post { uis.forEach { it.progress("Envoi à $to", out.name, sent, out.size) } }
                    }
                }
                Log.i(TAG, "Wave Drop : envoi ${if (ok) "termine" else "refuse"}")
                main.post { uis.forEach { it.finished(if (ok) "Envoyé à $to" else "$to a refusé", out.name, ok) } }
                if (ok) outgoing = null
            } catch (e: Exception) {
                Log.w(TAG, "Wave Drop : envoi impossible", e)
                main.post { uis.forEach { it.finished("Envoi impossible", e.message ?: "Erreur", false) } }
            } finally {
                try { sock?.close() } catch (e: Exception) { }
                busy.set(false)
            }
        }.start()
    }

    private data class Conn(val first: InputStream, val second: OutputStream, val third: String, val fourth: java.io.Closeable)

    /** Le meilleur chemin vers l'appareil : Wi-Fi local, sinon Bluetooth (RFCOMM vers un PC, L2CAP vers un telephone). */
    private fun connect(t: Nearby): Conn {
        val b = t.beacon
        val ip = b.ipString
        if (ip != null && b.port > 0 && wifiIp() != null) {
            try {
                val s = Socket()
                s.connect(InetSocketAddress(ip, b.port), 1500)
                s.soTimeout = 120_000
                return Conn(s.getInputStream(), s.getOutputStream(), "Wi-Fi", s)
            } catch (e: Exception) { Log.i(TAG, "Wave Drop : pas de Wi-Fi commun, passage au Bluetooth ($e)") }
        }
        val ad = adapter ?: error("Bluetooth indisponible")
        if (!hasPermissions(app)) error("autorise le Bluetooth dans Wave Drop")
        try { ad.cancelDiscovery() } catch (e: Exception) { }
        if (b.isPc && b.btAddr != null) {
            val addr = b.btAddr.joinToString(":") { "%02X".format(it) }
            val s = ad.getRemoteDevice(addr).createInsecureRfcommSocketToServiceRecord(UUID.fromString(WaveDropProtocol.RFCOMM_UUID))
            s.connect()
            return Conn(s.inputStream, s.outputStream, "Bluetooth", s)
        }
        val dev = t.device
        if (b.psm > 0 && dev != null && Build.VERSION.SDK_INT >= 29) {
            val s = dev.createInsecureL2capChannel(b.psm)
            s.connect()
            return Conn(s.inputStream, s.outputStream, "Bluetooth", s)
        }
        error("appareil injoignable (rapproche-le ou connecte-le au même Wi-Fi)")
    }

    /** Un PC sans Wi-Fi commun a un fichier pour nous : on se connecte a lui et il l'envoie. */
    private fun pullFrom(b: WaveDropProtocol.Beacon) {
        try {
            val n = nearby[b.id] ?: return
            val c = connect(n)
            handle(c.first, c.second, connector = true, c.third) { c.fourth.close() }
        } catch (e: Exception) {
            Log.w(TAG, "Wave Drop : recuperation impossible", e)
        } finally {
            main.postDelayed({ pulling.remove(b.id) }, 15_000)
        }
    }
}

/** Fichiers recus (pour l'onglet Wave Drop). */
object History {
    data class Item(val name: String, val from: String, val uri: String?, val time: Long)

    fun add(c: Context, name: String, from: String, uri: Uri?) {
        val p = c.getSharedPreferences("wavedrop", Context.MODE_PRIVATE)
        val lines = (p.getString("history", "") ?: "").split('\n').filter { it.isNotBlank() }.toMutableList()
        lines.add(0, listOf(name, from, uri?.toString() ?: "", System.currentTimeMillis().toString()).joinToString("\t") { it.replace('\t', ' ').replace('\n', ' ') })
        p.edit().putString("history", lines.take(30).joinToString("\n")).apply()
    }

    fun list(c: Context): List<Item> =
        (c.getSharedPreferences("wavedrop", Context.MODE_PRIVATE).getString("history", "") ?: "").split('\n').mapNotNull {
            val f = it.split('\t')
            if (f.size < 4) null else Item(f[0], f[1], f[2].ifBlank { null }, f[3].toLongOrNull() ?: 0)
        }
}
