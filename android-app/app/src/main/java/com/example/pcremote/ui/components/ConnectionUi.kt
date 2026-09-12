package com.example.pcremote.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore

/**
 * Connection banner shown in the shell while not CONNECTED. Each state gets a
 * distinct, semantically correct treatment (10-ERROR-HANDLING.md §2, §5):
 * reconnecting is progress, not an error; expected shutdown never offers retry.
 */
@Composable
fun ConnectionBanner(
    connection: RemoteConnection,
    state: ConnectionState,
    onRetry: () -> Unit,
    modifier: Modifier = Modifier
) {
    val ui = connection.uiState(state)
    when (ui) {
        is ConnectionUiState.Reconnecting -> Banner(
            container = MaterialTheme.colorScheme.secondaryContainer,
            content = MaterialTheme.colorScheme.onSecondaryContainer,
            leading = { CircularProgressIndicator(modifier = Modifier.padding(4.dp).size(16.dp)) },
            text = "Connection interrupted — reconnecting" +
                (ui.attempt.takeIf { it > 1 }?.let { " (attempt ${it})" } ?: "…"),
            actionLabel = null
        )
        is ConnectionUiState.Failed -> {
            if (ui.expectedShutdown) {
                Banner(
                    container = MaterialTheme.colorScheme.secondaryContainer,
                    content = MaterialTheme.colorScheme.onSecondaryContainer,
                    leading = null,
                    text = "PC is shutting down — the session will end shortly.",
                    actionLabel = null
                )
            } else {
                Banner(
                    container = MaterialTheme.colorScheme.errorContainer,
                    content = MaterialTheme.colorScheme.onErrorContainer,
                    leading = null,
                    text = "Couldn't connect — check the PC is on and reachable",
                    actionLabel = "Try again",
                    onAction = onRetry
                )
            }
        }
        is ConnectionUiState.Disconnected -> Banner(
            container = MaterialTheme.colorScheme.errorContainer,
            content = MaterialTheme.colorScheme.onErrorContainer,
            leading = null,
            text = "PC unavailable",
            actionLabel = "Try again",
            onAction = onRetry
        )
        else -> {}
    }
}

@Composable
private fun Banner(
    container: androidx.compose.ui.graphics.Color,
    content: androidx.compose.ui.graphics.Color,
    leading: (@Composable () -> Unit)?,
    text: String,
    actionLabel: String?,
    onAction: (() -> Unit)? = null
) {
    Surface(color = container, modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            leading?.invoke()
            Text(
                text,
                style = MaterialTheme.typography.bodySmall,
                color = content,
                modifier = Modifier.weight(1f).padding(start = if (leading != null) 8.dp else 0.dp)
            )
            if (actionLabel != null && onAction != null) {
                TextButton(onClick = onAction) {
                    Text(actionLabel, color = content, style = MaterialTheme.typography.labelLarge)
                }
            }
        }
    }
}

/**
 * Connection details (real data only — nothing is fabricated): name, status,
 * address, transport, and certificate pin state. Also the place to end the
 * session deliberately.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun ConnectionDetailsSheet(
    connection: RemoteConnection,
    settingsStore: SettingsStore,
    pinStore: PinStore,
    connState: ConnectionState,
    onDismiss: () -> Unit,
    onDisconnect: () -> Unit
) {
    androidx.compose.material3.ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 24.dp)
                .padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text("Connection", style = MaterialTheme.typography.titleLarge)
            val host = connection.currentHost
            DetailRow("PC", host?.let { settingsStore.getName(it) ?: it } ?: "—")
            DetailRow("Status", when (val ui = connection.uiState(connState)) {
                is ConnectionUiState.Connected -> "Connected"
                is ConnectionUiState.Connecting -> "Connecting…"
                is ConnectionUiState.Reconnecting -> "Reconnecting (attempt ${ui.attempt})"
                is ConnectionUiState.Failed ->
                    if (ui.expectedShutdown) "Shutting down" else "Failed"
                ConnectionUiState.Disconnected -> "Disconnected"
            })
            DetailRow("Address", host?.let { "$it:${connection.currentPort}" } ?: "—")
            DetailRow("Transport", "WSS (TLS)")
            DetailRow(
                "Certificate",
                if (host != null && pinStore.getPin(host) != null) "Pinned (trust-on-first-use)" else "Not pinned yet"
            )
            TextButton(onClick = onDisconnect) {
                Text("Disconnect and forget this session", color = MaterialTheme.colorScheme.error)
            }
        }
    }
}

@Composable
private fun DetailRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}