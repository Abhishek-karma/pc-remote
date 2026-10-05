package com.example.pcremote.settings

import android.content.Context

/**
 * The few things worth remembering between launches: how fast the cursor moves,
 * and whether the phone vibrates on tap.
 *
 * Plain SharedPreferences, not encrypted: this is not trust material, and nothing
 * here is worth a Keystore round trip.
 */
class Settings(context: Context) {

    private val prefs = context.getSharedPreferences("pc_remote_settings", Context.MODE_PRIVATE)

    var sensitivity: Float
        get() = prefs.getFloat(KEY_SENSITIVITY, DEFAULT_SENSITIVITY)
        set(value) {
            val clamped = value.coerceIn(MIN_SENSITIVITY, MAX_SENSITIVITY)
            prefs.edit().putFloat(KEY_SENSITIVITY, clamped).apply()
        }

    var haptics: Boolean
        get() = prefs.getBoolean(KEY_HAPTICS, true)
        set(value) = prefs.edit().putBoolean(KEY_HAPTICS, value).apply()

    companion object {
        const val MIN_SENSITIVITY = 0.5f
        const val MAX_SENSITIVITY = 3.0f
        const val DEFAULT_SENSITIVITY = 1.5f

        private const val KEY_SENSITIVITY = "sensitivity"
        private const val KEY_HAPTICS = "haptics"
    }
}