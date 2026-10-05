package com.example.pcremote.ui

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.example.pcremote.connection.Connection
import com.example.pcremote.connection.ConnectionState
import com.example.pcremote.connection.Keys
import com.example.pcremote.connection.Message
import com.example.pcremote.remote.Action
import com.example.pcremote.remote.TouchpadGestures

/**
 * The remote screen: a touchpad that fills the space, and a small row of keys
 * below it. Nothing else - no tabs, no cards, no decoration competing with the
 * pad the user is aiming with.
 */
@Composable
fun RemoteScreen(
    connection: Connection,
    state: ConnectionState,
    pcName: String,
    desktop: String,
    sensitivity: Float,
    haptics: Boolean,
    onOpenSettings: () -> Unit,
) {
    val gestures = remember(sensitivity) { TouchpadGestures(sensitivity = sensitivity) }

    // Modifiers the user has latched, so their buttons can show as held.
    var latched by remember { mutableStateOf(emptySet<String>()) }

    fun toggleModifier(name: String) {
        if (name in latched) {
            connection.key(name, "up")
            latched = latched - name
        } else {
            connection.key(name, "down")
            latched = latched + name
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        ConnectionHeader(
            pcName = pcName,
            state = state,
            desktop = desktop,
            onDisconnect = connection::disconnect,
            onOpenSettings = onOpenSettings,
        )

        // The touchpad takes everything that is left: it is the point of the app.
        Touchpad(
            modifier = Modifier.weight(1f).fillMaxWidth(),
            gestures = gestures,
            haptics = haptics,
            onAction = { action ->
                when (action) {
                    is Action.Move ->
                        connection.move(action.dx.toInt(), action.dy.toInt())

                    is Action.Scroll -> connection.scroll(action.notches)
                    Action.LeftClick -> connection.click("left")
                    Action.RightClick -> connection.click("right")
                    Action.HoldStart -> connection.buttonDown("left")
                    Action.HoldEnd -> connection.buttonUp("left")
                }
            },
        )

        KeyBar(
            latched = latched,
            onModifier = ::toggleModifier,
            onKey = { connection.key(it) },
            onText = connection::text,
        )
    }
}

/** Name, state and a way out. Two lines, no chrome. */
@Composable
private fun ConnectionHeader(
    pcName: String,
    state: ConnectionState,
    desktop: String,
    onDisconnect: () -> Unit,
    onOpenSettings: () -> Unit,
) {
    Surface(color = MaterialTheme.colorScheme.surfaceVariant) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = pcName.ifEmpty { "PC" },
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = statusLine(state, desktop),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            androidx.compose.material3.TextButton(onClick = onOpenSettings) {
                Text("Settings")
            }
            androidx.compose.material3.TextButton(onClick = onDisconnect) {
                Text("Disconnect")
            }
        }
    }
}

private fun statusLine(state: ConnectionState, desktop: String): String = when (state) {
    ConnectionState.CONNECTED -> desktopLabel(desktop)
    ConnectionState.CONNECTING -> "Connecting…"
    ConnectionState.PAIRING -> "Pairing needed"
    ConnectionState.RECONNECTING -> "Reconnecting…"
    ConnectionState.FAILED -> "Disconnected"
    ConnectionState.DISCONNECTED -> "Not connected"
}

/** The PC's desktop matters: typing into a lock screen is not a typo. */
private fun desktopLabel(desktop: String): String = when (desktop) {
    "locked" -> "Connected · PC is locked"
    "secure" -> "Connected · UAC prompt"
    "logon" -> "Connected · Windows sign-in"
    else -> "Connected"
}