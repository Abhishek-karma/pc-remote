package com.example.pcremote.connection

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * Everything the app remembers between launches, in one encrypted store:
 * trust tokens and pinned certificate fingerprints, both keyed by the PC's stable
 * id (never its IP, so a DHCP change does not force re-pairing).
 *
 * Tokens and pins are trust material, so this uses EncryptedSharedPreferences
 * backed by an Android Keystore key. If the keystore entry is ever lost - which
 * happens when the user changes their screen lock - the store is recreated empty
 * and the user simply pairs again.
 */
class PairedStore(context: Context) {

    private val prefs: SharedPreferences = try {
        encrypted(context)
    } catch (e: Exception) {
        // The keystore key is gone or unusable. Losing it is the same as losing
        // every pairing, so start clean rather than crash on launch.
        context.deleteSharedPreferences(FILE)
        encrypted(context)
    }

    fun token(pcId: String): String? = prefs.getString(key("token", pcId), null)

    fun saveToken(pcId: String, token: String) {
        prefs.edit().putString(key("token", pcId), token).apply()
    }

    fun pin(pcId: String): String? = prefs.getString(key("pin", pcId), null)

    /** Records the fingerprint on first pair only: a later, different certificate
     *  is a red flag, never something to silently accept. */
    fun savePin(pcId: String, fingerprint: String) {
        if (pin(pcId) == null) {
            prefs.edit().putString(key("pin", pcId), fingerprint).apply()
        }
    }

    fun isPaired(pcId: String): Boolean = token(pcId) != null

    /** Every paired PC id, for the "forget this PC" list in Settings. */
    fun pairedIds(): List<String> =
        prefs.all.keys.filter { it.startsWith(TOKEN_PREFIX) }
            .map { it.removePrefix(TOKEN_PREFIX) }
            .sorted()

    fun forget(pcId: String) {
        prefs.edit()
            .remove(key("token", pcId))
            .remove(key("pin", pcId))
            .apply()
    }

    private fun key(kind: String, pcId: String) = "$kind$pcId"

    private companion object {
        const val FILE = "pc_remote_paired"
        const val TOKEN_PREFIX = "token:"

        fun encrypted(context: Context): SharedPreferences {
            val masterKey = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            return EncryptedSharedPreferences.create(
                context,
                FILE,
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
            )
        }
    }
}