package com.example.pcremote

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.example.pcremote.connection.Connection
import com.example.pcremote.connection.PairedStore
import com.example.pcremote.discovery.Discovery
import com.example.pcremote.settings.Settings

/** Which of the three screens is showing. */
enum class Screen { LIST, REMOTE, SETTINGS }

/**
 * App state, owned by the Activity so it survives rotation.
 *
 * Deliberately one object rather than a layer of view models: there is one
 * connection, one discovery browse and one settings store, and none of them
 * needs a lifecycle of its own.
 */
class AppState(application: Application) {

    private val paired = PairedStore(application)
    private val settings = Settings(application)

    val connection = Connection(paired)
    val discovery = Discovery(application)

    var screen by mutableStateOf(Screen.LIST)

    var sensitivity by mutableFloatStateOf(settings.sensitivity)
        private set

    var haptics by mutableStateOf(settings.haptics)
        private set

    fun updateSensitivity(value: Float) {
        sensitivity = value
        settings.sensitivity = value
    }

    fun updateHaptics(value: Boolean) {
        haptics = value
        settings.haptics = value
    }

    fun connect(host: String, code: String? = null) {
        connection.connect(host, code)
        screen = Screen.REMOTE
    }

    fun goToList() {
        screen = Screen.LIST
    }

    /** Paired PC ids, for the Settings "forget this PC" list. */
    fun pairedNames(): List<String> = paired.pairedIds()

    fun forget(pcId: String) = paired.forget(pcId)
}