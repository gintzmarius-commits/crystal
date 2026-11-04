package com.liquidisland.drop

import android.app.Activity
import android.nfc.NfcAdapter
import android.nfc.Tag
import android.os.Bundle
import android.util.Log

/**
 * Invisible : Android l'ouvre quand le telephone touche une puce NFC (IsoDep) alors que le menu Wave Drop
 * est ferme. Si c'est un telephone Wave Drop, on echange les balises et le menu sort de l'ilot ;
 * sinon (carte bancaire, badge...) on se referme aussitot sans rien faire.
 */
class NfcTapActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WaveDrop.init(this)
        @Suppress("DEPRECATION")
        val tag: Tag? = intent?.getParcelableExtra(NfcAdapter.EXTRA_TAG)
        if (tag == null || !WaveDrop.enabled(this) || !WaveDrop.running) { finish(); overridePendingTransition(0, 0); return }
        Thread {
            val res = WaveDropHce.exchange(tag)
            runOnUiThread {
                if (res != null) {
                    Log.i("LiquidIsland", "Wave Drop : telephone colle (NFC, menu ferme) : ${res.second}")
                    WaveDrop.nfcPeer(res.first, res.second)
                    WaveDrop.touched(this)
                }
                finish()
                overridePendingTransition(0, 0)
            }
        }.start()
    }
}
