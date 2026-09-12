package com.example.pcremote.ui.components

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.RemoteConnection

/**
 * App shell top bar: PC name (or screen title) with a live connection status
 * line beneath it, plus trailing actions. Tapping the status opens connection
 * details. Replaces the old bare title + Settings text row (04 §7).
 */
@Composable
fun RemoteTopBar(
    title: String,
    connection: RemoteConnection,
    state: ConnectionState,
    onStatusClick: () -> Unit,
    actions: @Composable () -> Unit = {}
) {
    Surface(color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(horizontal = 16.dp, vertical = 8.dp)
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    title,
                    style = MaterialTheme.typography.titleLarge,
                    modifier = Modifier.weight(1f, fill = false)
                )
                actions()
            }
            ConnectionStatusLine(connection, state, Modifier.padding(top = 2.dp), onStatusClick)
        }
    }
}

@Composable
private fun ConnectionStatusLine(
    connection: RemoteConnection,
    state: ConnectionState,
    modifier: Modifier = Modifier,
    onStatusClick: () -> Unit
) {
    val ui = connection.uiState(state)
    val (label, clickable) = when (ui) {
        is ConnectionUiState.Connected -> "Connected" to true
        is ConnectionUiState.Connecting -> "Connecting…" to false
        is ConnectionUiState.Reconnecting ->
            "Reconnecting" + (ui.attempt.takeIf { it > 1 }?.let { " · attempt $it" } ?: "") + "…" to false
        is ConnectionUiState.Failed ->
            if (ui.expectedShutdown) "PC is shutting down" to false else "Disconnected" to true
        ConnectionUiState.Disconnected -> "Disconnected" to true
    }
    val dotColor = when (ui) {
        is ConnectionUiState.Connected -> StatusColors.positive()
        is ConnectionUiState.Connecting, is ConnectionUiState.Reconnecting -> StatusColors.pending()
        is ConnectionUiState.Failed ->
            if (ui.expectedShutdown) StatusColors.pending() else StatusColors.error()
        ConnectionUiState.Disconnected -> StatusColors.error()
    }
    Row(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .then(if (clickable) Modifier.clickable(onClick = onStatusClick) else Modifier),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        StatusDot(dotColor, description = label)
        Text(
            label,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        if (clickable) {
            Text(
                "· details",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.primary
            )
        }
    }
}