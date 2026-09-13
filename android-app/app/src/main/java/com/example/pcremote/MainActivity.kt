package com.example.pcremote

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
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
import com.example.pcremote.ui.components.ConnectionBanner
import com.example.pcremote.ui.components.ConnectionDetailsSheet
import com.example.pcremote.ui.components.ConnectionUiState
import com.example.pcremote.ui.components.RemoteIconButton
import com.example.pcremote.ui.components.RemoteTopBar
import com.example.pcremote.ui.components.uiState
import com.example.pcremote.ui.theme.RemoteTheme
import kotlinx.coroutines.launch

/** The four control destinations (04-UI-UX-SPECIFICATION.md §2). */
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
        val connection = RemoteConnection(tokenStore, pinStore) { host, name ->
            // The agent reports its machine name on auth_ok — remember it so
            // the UI shows the real PC name, including manual-IP pairings.
            settingsStore.setName(host, name)
        }

        setContent {
            RemoteTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    val connState by connection.state.collectAsState()
                    var sessionActive by rememberSaveable { mutableStateOf(false) }

                    // Derive session membership from the single connection
                    // state source — never a separate "connected" boolean.
                    LaunchedEffect(connState) {
                        when {
                            connState == ConnectionState.CONNECTED -> sessionActive = true
                            // Pairing is required again only when the token was
                            // rejected; expected shutdown ends the session too.
                            (connState == ConnectionState.FAILED && connection.lastAuthFailed) ||
                                (connState == ConnectionState.DISCONNECTED && connection.lastDisconnectExpected) ->
                                sessionActive = false
                        }
                    }

                    if (sessionActive) {
                        ControlHub(
                            connection = connection,
                            tokenStore = tokenStore,
                            settingsStore = settingsStore,
                            pinStore = pinStore,
                            connState = connState,
                            onSessionEnded = { sessionActive = false }
                        )
                    } else {
                        PairingScreen(
                            connection = connection,
                            tokenStore = tokenStore,
                            settingsStore = settingsStore,
                            pinStore = pinStore,
                            connState = connState,
                            onConnected = { sessionActive = true }
                        )
                    }
                }
            }
        }
    }
}

/**
 * Connected app shell: PC-name top bar with live status, connection banner,
 * active control screen, snackbar feedback, and an icon bottom nav
 * (04-UI-UX-SPECIFICATION.md §2, §7, §8).
 */
@Composable
private fun ControlHub(
    connection: RemoteConnection,
    tokenStore: TokenStore,
    settingsStore: SettingsStore,
    pinStore: PinStore,
    connState: ConnectionState,
    onSessionEnded: () -> Unit
) {
    var screen by rememberSaveable { mutableStateOf(AppScreen.Touchpad) }
    var showSettings by rememberSaveable { mutableStateOf(false) }
    var showDetails by rememberSaveable { mutableStateOf(false) }
    val sensitivity by settingsStore.sensitivity.collectAsState()
    val haptics by settingsStore.hapticsEnabled.collectAsState()
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

    val pcName = connection.currentHost?.let { settingsStore.getName(it) ?: it } ?: "PC Remote"

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

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            RemoteTopBar(
                title = pcName,
                connection = connection,
                state = connState,
                onStatusClick = { showDetails = true },
                actions = {
                    RemoteIconButton(
                        icon = Icons.Filled.Settings,
                        contentDescription = "Settings",
                        onClick = { showSettings = true }
                    )
                }
            )
        },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surfaceVariant) {
                AppScreen.entries.forEach { destination ->
                    val selected = destination == screen && !showSettings
                    NavigationBarItem(
                        selected = selected,
                        onClick = {
                            screen = destination
                            showSettings = false
                        },
                        icon = {
                            when (destination) {
                                AppScreen.Touchpad -> Icon(Icons.Filled.Mouse, contentDescription = null)
                                AppScreen.Keyboard -> Icon(Icons.Filled.Keyboard, contentDescription = null)
                                AppScreen.Media -> Icon(Icons.Filled.PlayCircle, contentDescription = null)
                                AppScreen.Power -> Icon(Icons.Filled.PowerSettingsNew, contentDescription = null)
                            }
                        },
                        label = { Text(destination.label) },
                        colors = NavigationBarItemDefaults.colors(
                            selectedTextColor = MaterialTheme.colorScheme.primary,
                            indicatorColor = MaterialTheme.colorScheme.primary,
                            unselectedIconColor = MaterialTheme.colorScheme.onSurfaceVariant,
                            unselectedTextColor = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    )
                }
            }
        }
    ) { padding ->
        if (showSettings) {
            SettingsScreen(
                tokenStore = tokenStore,
                settingsStore = settingsStore,
                connection = connection,
                onBack = { showSettings = false },
                modifier = Modifier.padding(padding)
            )
        } else {
            Column(modifier = Modifier.padding(padding)) {
                ConnectionBanner(connection, connState, onRetry = { connection.reconnectLast() })
                Box(modifier = Modifier.weight(1f)) {
                    when (screen) {
                        AppScreen.Touchpad -> TouchpadScreen(
                            connection = connection,
                            sensitivity = sensitivity,
                            hapticsEnabled = haptics,
                            settingsStore = settingsStore
                        )
                        AppScreen.Keyboard -> KeyboardScreen(connection)
                        AppScreen.Media -> MediaScreen(connection)
                        AppScreen.Power -> PowerScreen(
                            connection = connection,
                            pcName = pcName,
                            onFeedback = { message ->
                                scope.launch { snackbarHostState.showSnackbar(message) }
                            }
                        )
                    }
                }
            }
        }
    }

    if (showDetails) {
        ConnectionDetailsSheet(
            connection = connection,
            settingsStore = settingsStore,
            pinStore = pinStore,
            connState = connState,
            onDismiss = { showDetails = false },
            onDisconnect = {
                showDetails = false
                connection.disconnect()
                onSessionEnded()
            }
        )
    }
}