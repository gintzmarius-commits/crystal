package com.liquidisland.drop

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.ServerSocket
import java.net.Socket
import java.util.Random
import java.util.concurrent.TimeUnit

/** Le protocole Wave Drop : entre deux telephones (Kotlin <-> Kotlin) et avec le PC (Kotlin <-> C#). */
class WaveDropProtocolTest {

    private fun randomBytes(n: Int) = ByteArray(n).also { Random(42).nextBytes(it) }

    @Test
    fun beaconRoundTrip() {
        val b = WaveDropProtocol.Beacon(0x12345678, true, byteArrayOf(192.toByte(), 168.toByte(), 1, 42), 50123, byteArrayOf(1, 2, 3, 4, 5, 6), 0, 0x0A0B0C0D)
        val d = WaveDropProtocol.Beacon.decode(b.encode())!!
        assertEquals(b.id, d.id)
        assertEquals("192.168.1.42", d.ipString)
        assertEquals(50123, d.port)
        assertArrayEquals(b.btAddr, d.btAddr)
        assertEquals(0x0A0B0C0D, d.offerFor)
        val p = WaveDropProtocol.Beacon(-2, false, null, 0, null, 0x81, 0)
        val dp = WaveDropProtocol.Beacon.decode(p.encode())!!
        assertEquals(0x81, dp.psm)
        assertEquals(null, dp.ip)
    }

    @Test
    fun mapEscapes() {
        val m = mapOf("name" to "Photo d'été = 100% ✓\nligne 2", "x" to "")
        assertEquals(m, WaveDropProtocol.decodeMap(WaveDropProtocol.encodeMap(m)))
    }

    /** Telephone -> telephone (les deux en Kotlin), par une vraie connexion reseau locale. */
    @Test
    fun kotlinToKotlin() {
        val data = randomBytes(300_000)
        val server = ServerSocket(0)
        var received: ByteArray? = null
        val t = Thread {
            server.accept().use { sock ->
                val s = WaveDropProtocol.Session(sock.getInputStream(), sock.getOutputStream(), connector = false)
                s.handshake(mapOf("name" to "Recepteur", "platform" to "android", "id" to "2", "offer" to "0"))
                val out = ByteArrayOutputStream()
                val offer = WaveDropProtocol.receiveFile(s, { true }, { out }, {})
                assertEquals("photo.jpg", offer!!.name)
                received = out.toByteArray()
            }
        }
        t.start()
        Socket("127.0.0.1", server.localPort).use { sock ->
            val s = WaveDropProtocol.Session(sock.getInputStream(), sock.getOutputStream(), connector = true)
            s.handshake(mapOf("name" to "Envoyeur", "platform" to "android", "id" to "1", "offer" to "1"))
            assertEquals("Recepteur", s.peer["name"])
            assertTrue(WaveDropProtocol.sendFile(s, WaveDropProtocol.Offer("photo.jpg", data.size.toLong(), "image/jpeg"), data.inputStream()) {})
        }
        t.join(10_000)
        assertArrayEquals(data, received)
    }

    /** Refus : rien n'est envoye. */
    @Test
    fun refused() {
        val server = ServerSocket(0)
        val t = Thread {
            server.accept().use { sock ->
                val s = WaveDropProtocol.Session(sock.getInputStream(), sock.getOutputStream(), connector = false)
                s.handshake(mapOf("name" to "R", "platform" to "android", "id" to "2", "offer" to "0"))
                assertEquals(null, WaveDropProtocol.receiveFile(s, { false }, { ByteArrayOutputStream() }, {}))
            }
        }
        t.start()
        Socket("127.0.0.1", server.localPort).use { sock ->
            val s = WaveDropProtocol.Session(sock.getInputStream(), sock.getOutputStream(), connector = true)
            s.handshake(mapOf("name" to "E", "platform" to "android", "id" to "1", "offer" to "1"))
            assertEquals(false, WaveDropProtocol.sendFile(s, WaveDropProtocol.Offer("a.txt", 3, "text/plain"), byteArrayOf(1, 2, 3).inputStream()) {})
        }
        t.join(10_000)
    }

    // ---------- Avec le programme C# (meme protocole que sur PC) ----------

    private fun interop(): String? = System.getenv("WD_INTEROP")

    private fun dotnet(vararg args: String): Process =
        ProcessBuilder(listOf("dotnet", interop()!!) + args).redirectErrorStream(true).start()

    @Test
    fun beaconSameBytesAsPc() {
        assumeTrue(interop() != null)
        val p = dotnet("beacon")
        val lines = p.inputStream.bufferedReader().readLines()
        p.waitFor(30, TimeUnit.SECONDS)
        val b = WaveDropProtocol.Beacon(0x12345678, true, byteArrayOf(192.toByte(), 168.toByte(), 1, 42), 50123, byteArrayOf(1, 2, 3, 4, 5, 6), 0, 0x0A0B0C0D)
        assertEquals(lines[0], b.encode().joinToString("") { "%02X".format(it) })
        val ph = WaveDropProtocol.Beacon(-2, false, null, 0, null, 0x81, 0)
        assertEquals(lines[1], ph.encode().joinToString("") { "%02X".format(it) })
    }

    /** PC -> telephone */
    @Test
    fun pcSendsToPhone() {
        assumeTrue(interop() != null)
        val data = randomBytes(1_000_000)
        val f = File.createTempFile("wd", ".bin").also { it.writeBytes(data) }
        val server = ServerSocket(0)
        val proc = dotnet("send", "127.0.0.1", server.localPort.toString(), f.absolutePath)
        server.soTimeout = 30_000
        val out = ByteArrayOutputStream()
        server.accept().use { sock ->
            val s = WaveDropProtocol.Session(sock.getInputStream(), sock.getOutputStream(), connector = false)
            s.handshake(mapOf("name" to "Xiaomi", "platform" to "android", "id" to "3", "offer" to "0"))
            assertEquals("PC de test", s.peer["name"])
            WaveDropProtocol.receiveFile(s, { it.size == data.size.toLong() }, { out }, {})
        }
        val log = proc.inputStream.bufferedReader().readText()
        proc.waitFor(30, TimeUnit.SECONDS)
        println(log)
        assertTrue(log, log.contains("SENT Xiaomi"))
        assertArrayEquals(data, out.toByteArray())
    }

    /** Telephone -> PC */
    @Test
    fun phoneSendsToPc() {
        assumeTrue(interop() != null)
        val data = randomBytes(700_001)
        val port = ServerSocket(0).use { it.localPort }
        val target = File.createTempFile("wd-recu", ".bin")
        val proc = dotnet("receive", port.toString(), target.absolutePath)
        val reader = proc.inputStream.bufferedReader()
        assertEquals("READY", reader.readLine())
        Socket("127.0.0.1", port).use { sock ->
            val s = WaveDropProtocol.Session(sock.getInputStream(), sock.getOutputStream(), connector = true)
            s.handshake(mapOf("name" to "Xiaomi", "platform" to "android", "id" to "3", "offer" to "1"))
            assertTrue(WaveDropProtocol.sendFile(s, WaveDropProtocol.Offer("vidéo été.mp4", data.size.toLong(), "video/mp4"), data.inputStream()) {})
        }
        val log = reader.readText()
        proc.waitFor(30, TimeUnit.SECONDS)
        println(log)
        assertTrue(log, log.contains("RECEIVED vidéo été.mp4"))
        assertArrayEquals(data, target.readBytes())
    }
}
