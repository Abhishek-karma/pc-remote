package com.example.pcremote.ui.components

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
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
 * App shell top bar, matching the product mock: PC name with the status dot
 * top-right, the plain status line beneath it (tapping it opens connection
 * details), plus trailing actions.
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
            val ui = connection.uiState(state)
            val label = when (ui) {
                is ConnectionUiState.Connected -> "Connected"
                is ConnectionUiState.Connecting -> "Connecting…"
                is ConnectionUiState.Reconnecting ->
                    "Reconnecting" + (ui.attempt.takeIf { it > 1 }?.let { " · attempt $it" } ?: "") + "…"
                is ConnectionUiState.Failed ->
                    if (ui.expectedShutdown) "PC is shutting down" else "Disconnected"
                ConnectionUiState.Disconnected -> "Disconnected"
            }
            val dotColor = when (ui) {
                is ConnectionUiState.Connected -> StatusColors.positive()
                is ConnectionUiState.Connecting, is ConnectionUiState.Reconnecting -> StatusColors.pending()
                is ConnectionUiState.Failed ->
                    if (ui.expectedShutdown) StatusColors.pending() else StatusColors.error()
                ConnectionUiState.Disconnected -> StatusColors.error()
            }
            val clickable = ui is ConnectionUiState.Connected ||
                ui is ConnectionUiState.Disconnected ||
                (ui is ConnectionUiState.Failed && !ui.expectedShutdown)

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
                Spacer(modifier = Modifier.width(12.dp))
                StatusDot(dotColor, description = label)
                actions()
            }
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 40.dp)
                    .then(if (clickable) Modifier.clickable(onClick = onStatusClick) else Modifier),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    label,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}
