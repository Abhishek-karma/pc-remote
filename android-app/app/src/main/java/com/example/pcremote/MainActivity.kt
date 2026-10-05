package com.example.pcremote

import android.Manifest
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
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
 * product.
 */
class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // POST_NOTIFICATIONS is only used so a dropped connection can be
        // reported; the app is fully usable if the user declines.
        val requestNotifications =
            registerForActivityResult(ActivityResultContracts.RequestPermission()) { }
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            requestNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        setContent {
            MaterialTheme {
                Surface { App(remember { AppState(application) }) }
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

    // Discovery only runs while the user is picking a PC.
    DisposableEffect(state) {
        state.discovery.start()
        onDispose { state.discovery.stop() }
    }

    when (state.screen) {
        Screen.SETTINGS -> SettingsScreen(
            sensitivity = state.sensitivity,
            onSensitivity = state::updateSensitivity,
            haptics = state.haptics,
            onHaptics = state::updateHaptics,
            pairedNames = state.pairedNames(),
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
                onSelect = { pc, code -> state.connect(pc.host, code) },
                onManual = { host, code -> state.connect(host, code) },
                onRetryDiscovery = state.discovery::start,
            )
        }
    }
}