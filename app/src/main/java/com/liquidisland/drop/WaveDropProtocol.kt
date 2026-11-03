package com.liquidisland.drop

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.Inet4Address
import java.net.InetAddress
import java.nio.ByteBuffer
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Wave Drop : protocole commun Android / Windows (le meme est ecrit en C# dans WaveDropProtocol.cs).
 *
 * 1. Detection : chaque appareil diffuse une petite balise Bluetooth (ou la donne par NFC) avec son
 *    identifiant, son adresse Wi-Fi locale et ce qu'il faut pour le joindre en Bluetooth.
 * 2. Connexion (Wi-Fi si possible, sinon Bluetooth) puis poignee de main : chacun envoie sa cle publique
 *    (ECDH P-256), une cle AES-256 commune en est tiree ; tout le reste est chiffre (AES-GCM).
 * 3. L'appareil qui a un fichier propose (nom, taille, type) ; l'autre doit ACCEPTER ; puis le fichier passe
 *    par morceaux de 64 Ko, avec une empreinte SHA-256 verifiee a la fin.
 *
 * Ce fichier n'utilise que Java standard (teste en dehors d'Android contre la version C#).
 */
object WaveDropProtocol {
    const val COMPANY_ID = 0xFFFF
    const val RFCOMM_UUID = "7a1e0001-5d2b-4c8e-9f3a-576176654472"
    val AID = byteArrayOf(0xF0.toByte(), 0x57, 0x41, 0x56, 0x45, 0x44, 0x52, 0x4F, 0x50) // F0 + "WAVEDROP"
    const val CHUNK = 64 * 1024
    private val MAGIC = "WDP1".toByteArray()
    private val KDF_LABEL = "WaveDrop1".toByteArray()

    // ---------- Balise (23 octets) ----------

    /** Ce qu'un appareil annonce autour de lui. */
    data class Beacon(
        val id: Int,
        val isPc: Boolean,
        val ip: ByteArray?,          // IPv4 sur le Wi-Fi local (null = pas de Wi-Fi)
        val port: Int,
        val btAddr: ByteArray?,      // adresse Bluetooth classique (PC) : le telephone s'y connecte en RFCOMM
        val psm: Int,                // canal Bluetooth L2CAP (telephone) : un autre telephone s'y connecte
        val offerFor: Int            // != 0 : « j'ai un fichier pour cet appareil » (le telephone vient le chercher)
    ) {
        fun encode(): ByteArray {
            val b = ByteBuffer.allocate(23)
            b.put('W'.code.toByte()).put('D'.code.toByte())
            var flags = 0x10 // bit 4 : version 1 du protocole
            if (ip != null) flags = flags or 0x01
            if (btAddr != null) flags = flags or 0x02
            if (psm > 0) flags = flags or 0x04
            if (offerFor != 0) flags = flags or 0x08
            if (isPc) flags = flags or 0x20
            b.put(flags.toByte())
            b.putInt(id)
            b.put(ip ?: ByteArray(4))
            b.putShort(port.toShort())
            when {
                btAddr != null -> b.put(btAddr, 0, 6)
                else -> { b.putShort(psm.toShort()); b.put(ByteArray(4)) }
            }
            b.putInt(offerFor)
            return b.array()
        }

        val ipString: String? get() = ip?.let { InetAddress.getByAddress(it).hostAddress }

        companion object {
            fun decode(d: ByteArray?): Beacon? {
                if (d == null || d.size < 23 || d[0] != 'W'.code.toByte() || d[1] != 'D'.code.toByte()) return null
                val b = ByteBuffer.wrap(d)
                b.position(2)
                val flags = b.get().toInt() and 0xFF
                if (flags and 0x10 == 0) return null
                val id = b.int
                val ip = ByteArray(4).also { b.get(it) }
                val port = b.short.toInt() and 0xFFFF
                val rest = ByteArray(6).also { b.get(it) }
                val offer = b.int
                val hasBt = flags and 0x02 != 0
                val psm = if (flags and 0x04 != 0) ((rest[0].toInt() and 0xFF) shl 8) or (rest[1].toInt() and 0xFF) else 0
                return Beacon(
                    id, flags and 0x20 != 0,
                    if (flags and 0x01 != 0) ip else null, port,
                    if (hasBt) rest else null, psm,
                    if (flags and 0x08 != 0) offer else 0
                )
            }
        }
    }

    /** Adresse IPv4 en 4 octets (null si ce n'est pas de l'IPv4). */
    fun ipv4(a: InetAddress?): ByteArray? = (a as? Inet4Address)?.address

    // ---------- Messages (cle=valeur, chiffres une fois la connexion securisee) ----------

    fun encodeMap(m: Map<String, String>): ByteArray {
        val sb = StringBuilder()
        for ((k, v) in m) sb.append(esc(k)).append('=').append(esc(v)).append('\n')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    fun decodeMap(b: ByteArray): Map<String, String> {
        val m = LinkedHashMap<String, String>()
        for (line in String(b, Charsets.UTF_8).split('\n')) {
            val i = line.indexOf('=')
            if (i > 0) m[unesc(line.substring(0, i))] = unesc(line.substring(i + 1))
        }
        return m
    }

    private fun esc(s: String): String {
        val sb = StringBuilder()
        for (byte in s.toByteArray(Charsets.UTF_8)) {
            val c = byte.toInt() and 0xFF
            val ch = c.toChar()
            if (ch in 'a'..'z' || ch in 'A'..'Z' || ch in '0'..'9' || ch == '-' || ch == '_' || ch == '.') sb.append(ch)
            else sb.append('%').append("0123456789ABCDEF"[c shr 4]).append("0123456789ABCDEF"[c and 15])
        }
        return sb.toString()
    }

    private fun unesc(s: String): String {
        val out = java.io.ByteArrayOutputStream()
        var i = 0
        while (i < s.length) {
            val ch = s[i]
            if (ch == '%' && i + 2 < s.length) {
                out.write(s.substring(i + 1, i + 3).toInt(16)); i += 3
            } else { out.write(ch.code); i++ }
        }
        return String(out.toByteArray(), Charsets.UTF_8)
    }

    // ---------- Session securisee ----------

    class Session(
        input: InputStream, output: OutputStream,
        /** true pour l'appareil qui a ouvert la connexion */
        private val connector: Boolean
    ) {
        private val inp = DataInputStream(input)
        private val out = DataOutputStream(output)
        private var key: SecretKeySpec? = null
        private var sendCounter = 0L
        private var recvCounter = 0L
        lateinit var peer: Map<String, String>
            private set

        /** Echange des presentations et des cles. mine : name, platform, id, offer (0/1). */
        fun handshake(mine: Map<String, String>) {
            val kpg = KeyPairGenerator.getInstance("EC")
            kpg.initialize(ECGenParameterSpec("secp256r1"))
            val kp = kpg.generateKeyPair()
            val hello = LinkedHashMap(mine)
            hello["v"] = "1"
            hello["pub"] = Base64.getEncoder().encodeToString(kp.public.encoded)
            val mineFrame = MAGIC + encodeMap(hello)
            if (connector) { writeFrame(mineFrame); peer = readHello() } else { peer = readHello(); writeFrame(mineFrame) }
            val pub = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(peer["pub"] ?: error("cle absente"))))
            val ka = KeyAgreement.getInstance("ECDH")
            ka.init(kp.private)
            ka.doPhase(pub, true)
            val z = ka.generateSecret()
            val md = MessageDigest.getInstance("SHA-256")
            md.update(z)
            md.update(KDF_LABEL)
            key = SecretKeySpec(md.digest(), "AES")
        }

        private fun readHello(): Map<String, String> {
            val f = readFrame()
            if (f.size < 4 || !f.copyOfRange(0, 4).contentEquals(MAGIC)) error("pas un appareil Wave Drop")
            return decodeMap(f.copyOfRange(4, f.size))
        }

        private fun writeFrame(b: ByteArray) {
            out.writeInt(b.size)
            out.write(b)
            out.flush()
        }

        private fun readFrame(): ByteArray {
            val n = inp.readInt()
            if (n < 0 || n > CHUNK + 1024) error("trame invalide")
            return ByteArray(n).also { inp.readFully(it) }
        }

        private fun nonce(mine: Boolean, counter: Long): ByteArray {
            // 4 octets de sens (qui envoie) + 8 octets de compteur : jamais deux fois le meme
            val fromConnector = if (mine) connector else !connector
            return ByteBuffer.allocate(12).putInt(if (fromConnector) 1 else 2).putLong(counter).array()
        }

        /** type : 1 = message, 2 = morceau de fichier */
        fun send(type: Int, body: ByteArray, len: Int = body.size) {
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(128, nonce(true, sendCounter++)))
            val plain = ByteArray(len + 1)
            plain[0] = type.toByte()
            System.arraycopy(body, 0, plain, 1, len)
            writeFrame(c.doFinal(plain))
        }

        /** Renvoie (type, contenu) ; erreur si le message a ete modifie. */
        fun receive(): Pair<Int, ByteArray> {
            val f = readFrame()
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, nonce(false, recvCounter++)))
            val plain = c.doFinal(f)
            return (plain[0].toInt() and 0xFF) to plain.copyOfRange(1, plain.size)
        }

        fun sendMap(m: Map<String, String>) = send(1, encodeMap(m))

        fun receiveMap(): Map<String, String> {
            val (t, b) = receive()
            if (t != 1) error("message inattendu")
            return decodeMap(b)
        }
    }

    // ---------- Transfert ----------

    data class Offer(val name: String, val size: Long, val mime: String)

    /** Envoie un fichier ; renvoie false si l'autre a refuse. */
    fun sendFile(s: Session, offer: Offer, source: InputStream, progress: (Long) -> Unit): Boolean {
        s.sendMap(mapOf("t" to "offer", "name" to offer.name, "size" to offer.size.toString(), "mime" to offer.mime))
        val answer = s.receiveMap()
        if (answer["ok"] != "1") return false
        val md = MessageDigest.getInstance("SHA-256")
        val buf = ByteArray(CHUNK)
        var sent = 0L
        while (true) {
            val n = readSome(source, buf)
            if (n <= 0) break
            md.update(buf, 0, n)
            s.send(2, buf, n)
            sent += n
            progress(sent)
        }
        s.sendMap(mapOf("t" to "end", "sha" to Base64.getEncoder().encodeToString(md.digest()), "size" to sent.toString()))
        val done = s.receiveMap()
        if (done["ok"] != "1") error("le fichier est arrive abime")
        return true
    }

    private fun readSome(i: InputStream, buf: ByteArray): Int {
        var total = 0
        while (total < buf.size) {
            val n = i.read(buf, total, buf.size - total)
            if (n < 0) break
            total += n
        }
        return total
    }

    /**
     * Recoit un fichier : ask(offre) demande a l'utilisateur (true = accepte), sink(offre) donne ou ecrire.
     * Renvoie l'offre recue si tout s'est bien passe, null si refuse.
     */
    fun receiveFile(s: Session, ask: (Offer) -> Boolean, sink: (Offer) -> OutputStream, progress: (Long) -> Unit): Offer? {
        val m = s.receiveMap()
        if (m["t"] != "offer") error("offre attendue")
        val offer = Offer(m["name"] ?: "fichier", m["size"]?.toLongOrNull() ?: -1, m["mime"] ?: "application/octet-stream")
        val ok = ask(offer)
        s.sendMap(mapOf("t" to "answer", "ok" to if (ok) "1" else "0"))
        if (!ok) return null
        val md = MessageDigest.getInstance("SHA-256")
        var got = 0L
        sink(offer).use { o ->
            while (true) {
                val (t, b) = s.receive()
                if (t == 2) {
                    md.update(b)
                    o.write(b)
                    got += b.size
                    progress(got)
                } else {
                    val end = decodeMap(b)
                    val good = end["t"] == "end" && end["sha"] == Base64.getEncoder().encodeToString(md.digest())
                    s.sendMap(mapOf("t" to "done", "ok" to if (good) "1" else "0"))
                    if (!good) error("empreinte differente : fichier abime")
                    break
                }
            }
        }
        return offer
    }
}
