package com.example.pcremote

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import com.example.pcremote.connection.ConnectionState
import com.example.pcremote.ui.PcListScreen
import com.example.pcremote.ui.RemoteScreen
import com.example.pcremote.ui.SettingsScreen

/**
 * The whole app is three screens and one flow:
 *
 *   PC list -> connect -> remote
 *
 * No navigation graph, no tab bar, no dashboard. The touchpad screen is the
 * product. State lives in the Application ([PcRemoteApp]), so recreating this
 * Activity never drops the connection.
 */
class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            MaterialTheme {
                Surface { App((application as PcRemoteApp).state) }
            }
        }
    }
}

@Composable
private fun App(state: AppState) {
    val connection by state.connection.state.collectAsState()
    val pcName by state.connection.pcName.collectAsState()
    val desktop by state.connection.desktop.collectAsState()
    val discovered by state.discovery.pcs.collectAsState()
    val discoveryStatus by state.discovery.status.collectAsState()

    // Discovery only needs to run while the user picks a PC, but NSD browsing
    // is cheap and stopping it mid-lookup loses resolved PCs; run it for the
    // Activity's lifetime.
    androidx.compose.runtime.DisposableEffect(state) {
        state.discovery.start()
        onDispose { state.discovery.stop() }
    }

    when (state.screen) {
        Screen.SETTINGS -> SettingsScreen(
            sensitivity = state.sensitivity,
            onSensitivity = state::updateSensitivity,
            haptics = state.haptics,
            onHaptics = state::updateHaptics,
            pairedNames = state.pairedIds(),
            onForget = state::forget,
            onBack = { state.goToList() },
        )

        else -> if (connection == ConnectionState.CONNECTED) {
            RemoteScreen(
                connection = state.connection,
                state = connection,
                pcName = pcName,
                desktop = desktop,
                sensitivity = state.sensitivity,
                haptics = state.haptics,
                onOpenSettings = { state.screen = Screen.SETTINGS },
            )
        } else {
            PcListScreen(
                pcs = discovered,
                status = discoveryStatus,
                isPaired = state::isPaired,
                onConnect = { host, pcId, code -> state.connect(host, code, pcId) },
                onRetryDiscovery = state.discovery::start,
            )
        }
    }
}
