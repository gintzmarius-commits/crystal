package com.liquidisland.drop

import android.nfc.cardemulation.HostApduService
import android.os.Bundle
import android.util.Log

/**
 * Le telephone qui RECOIT se comporte comme une petite carte NFC : quand l'autre (celui qui envoie)
 * se colle a lui, il y lit la balise Wave Drop et le nom de l'appareil.
 */
class WaveDropHce : HostApduService() {

    override fun processCommandApdu(cmd: ByteArray, extras: Bundle?): ByteArray {
        WaveDrop.init(this)
        // SELECT de l'identifiant Wave Drop : 00 A4 04 00 Lc AID
        if (cmd.size >= 5 && cmd[1] == 0xA4.toByte() && cmd[2] == 0x04.toByte()) {
            if (!WaveDrop.running) return byteArrayOf(0x6A, 0x82.toByte())
            val beacon = WaveDrop.beacon().encode()
            val name = WaveDrop.myName.toByteArray(Charsets.UTF_8).let { if (it.size > 80) it.copyOf(80) else it }
            Log.i("LiquidIsland", "Wave Drop : lu par un telephone colle (NFC)")
            return beacon + byteArrayOf(name.size.toByte()) + name + byteArrayOf(0x90.toByte(), 0x00)
        }
        return byteArrayOf(0x6D, 0x00)
    }

    override fun onDeactivated(reason: Int) {}

    companion object {
        /** Commande SELECT envoyee par le telephone qui envoie. */
        fun selectApdu(): ByteArray {
            val aid = WaveDropProtocol.AID
            return byteArrayOf(0x00, 0xA4.toByte(), 0x04, 0x00, aid.size.toByte()) + aid + byteArrayOf(0x00)
        }

        /** Lit la reponse : balise + nom (null si ce n'est pas un appareil Wave Drop actif). */
        fun parse(resp: ByteArray?): Pair<WaveDropProtocol.Beacon, String>? {
            if (resp == null || resp.size < 26) return null
            if (resp[resp.size - 2] != 0x90.toByte() || resp[resp.size - 1] != 0x00.toByte()) return null
            val b = WaveDropProtocol.Beacon.decode(resp.copyOfRange(0, 23)) ?: return null
            val n = resp[23].toInt() and 0xFF
            if (24 + n > resp.size - 2) return null
            return b to String(resp, 24, n, Charsets.UTF_8)
        }
    }
}
