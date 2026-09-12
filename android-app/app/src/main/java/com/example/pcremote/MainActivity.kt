package com.example.pcremote

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.EncryptedPrefs
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.network.TokenStore
import com.example.pcremote.service.ConnectionForegroundService
import com.example.pcremote.ui.KeyboardScreen
import com.example.pcremote.ui.MediaScreen
import com.example.pcremote.ui.PairingScreen
import com.example.pcremote.ui.PowerScreen
import com.example.pcremote.ui.SettingsScreen
import com.example.pcremote.ui.TouchpadScreen
import com.example.pcremote.ui.theme.RemoteTheme

/** The four control destinations shown in the bottom nav (04-UI-UX-SPECIFICATION.md §2). */
enum class AppScreen(val label: String) {
    Touchpad("Touchpad"),
    Keyboard("Keyboard"),
    Media("Media"),
    Power("Power")
}

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Encrypted storage for tokens + cert pins (09-SECURITY-PRIVACY.md §4).
        val prefs = EncryptedPrefs.create(this)
        val tokenStore = TokenStore(prefs)
        val settingsStore = SettingsStore(prefs)
        val pinStore = PinStore(prefs)
        val connection = RemoteConnection(tokenStore, pinStore)

        setContent {
            RemoteTheme {
                Surface(
                    modifier = Modifier
                        .fillMaxSize()
                        // targetSdk 35 enforces edge-to-edge: content draws under
                        // the status bar and gesture nav bar unless we inset for
                        // them (status bar top, nav bar + cutouts bottom/sides).
                        .safeDrawingPadding()
                ) {
                    var connected by remember { mutableStateOf(false) }

                    if (connected) {
                        ControlHub(
                            connection = connection,
                            tokenStore = tokenStore,
                            settingsStore = settingsStore
                        )
                    } else {
                        PairingScreen(
                            connection = connection,
                            tokenStore = tokenStore,
                            onConnected = { connected = true }
                        )
                    }
                }
            }
        }
    }
}

/**
 * The post-pairing shell: title row with Settings, a thin connection-status
 * banner (04-UI-UX-SPECIFICATION.md §2, 10-ERROR-HANDLING.md §5), the active
 * control screen, and a text-only bottom nav. The foreground service runs
 * while CONNECTED so an active session survives backgrounding.
 */
@Composable
private fun ControlHub(
    connection: RemoteConnection,
    tokenStore: TokenStore,
    settingsStore: SettingsStore
) {
    var screen by rememberSaveable { mutableStateOf(AppScreen.Touchpad) }
    var showSettings by rememberSaveable { mutableStateOf(false) }
    val sensitivity by settingsStore.sensitivity.collectAsState()
    val connState by connection.state.collectAsState()
    val context = LocalContext.current

    // Foreground service + notification permission while a session is active.
    val notificationPermissionLauncher =
        androidx.activity.compose.rememberLauncherForActivityResult(
            ActivityResultContracts.RequestPermission()
        ) { }
    LaunchedEffect(connState == ConnectionState.CONNECTED) {
        if (connState == ConnectionState.CONNECTED) {
            if (Build.VERSION.SDK_INT >= 33 &&
                ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
            ContextCompat.startForegroundService(
                context, Intent(context, ConnectionForegroundService::class.java)
            )
        } else {
            context.stopService(Intent(context, ConnectionForegroundService::class.java))
        }
    }

    if (showSettings) {
        SettingsScreen(
            tokenStore = tokenStore,
            settingsStore = settingsStore,
            onBack = { showSettings = false }
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(screen.label, style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = { showSettings = true }) { Text("Settings") }
        }

        when (connState) {
            ConnectionState.RECONNECTING -> StatusBanner(
                "Reconnecting…",
                onClick = { connection.reconnectLast() }
            )
            ConnectionState.CONNECTING, ConnectionState.FAILED, ConnectionState.DISCONNECTED ->
                StatusBanner(
                    if (connection.lastDisconnectExpected) "PC is shutting down…"
                    else "Disconnected — tap to retry",
                    onClick = { connection.reconnectLast() }
                )
            else -> {}
        }

        Box(modifier = Modifier.weight(1f)) {
            when (screen) {
                AppScreen.Touchpad -> TouchpadScreen(connection, sensitivity = sensitivity)
                AppScreen.Keyboard -> KeyboardScreen(connection)
                AppScreen.Media -> MediaScreen(connection)
                AppScreen.Power -> PowerScreen(connection)
            }
        }

        // Bottom nav: a filled, elevated bar (not floating text) sitting flush
        // on the safe-area edge. Text-only items keep the no-icon dependency
        // stance; selection is shown by accent color + weight, same size so the
        // layout doesn't jump.
        Surface(
            color = MaterialTheme.colorScheme.surfaceVariant,
            tonalElevation = 2.dp,
            modifier = Modifier.fillMaxWidth()
        ) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                AppScreen.entries.forEach { destination ->
                    val isSelected = destination == screen
                    TextButton(
                        onClick = { screen = destination },
                        modifier = Modifier
                            .weight(1f)
                            .heightIn(min = 52.dp)
                            .semantics { selected = isSelected },
                        colors = androidx.compose.material3.ButtonDefaults.textButtonColors(
                            contentColor = if (isSelected) MaterialTheme.colorScheme.primary
                            else MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    ) {
                        Text(
                            destination.label,
                            style = MaterialTheme.typography.titleSmall,
                            fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Normal
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun StatusBanner(text: String, onClick: () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick)
    ) {
        Text(
            text = "$text — tap for details",
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onErrorContainer
        )
    }
}