package com.example.pcremote.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.PowerSettingsNew
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.ui.theme.RemoteColors

/**
 * Connection banner shown in the shell while not CONNECTED.
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
            container = RemoteColors.WarningContainer.copy(alpha = 0.35f),
            content = RemoteColors.Warning,
            leading = {
                CircularProgressIndicator(
                    modifier = Modifier.padding(4.dp).size(14.dp),
                    strokeWidth = 2.dp,
                    color = RemoteColors.Warning
                )
            },
            text = "Reconnecting to PC" +
                (ui.attempt.takeIf { it > 1 }?.let { " (attempt $it)" } ?: "…"),
            actionLabel = null
        )
        is ConnectionUiState.Failed -> {
            if (ui.expectedShutdown) {
                Banner(
                    container = MaterialTheme.colorScheme.secondaryContainer,
                    content = MaterialTheme.colorScheme.onSecondaryContainer,
                    leading = null,
                    text = "PC is shutting down — session will end shortly.",
                    actionLabel = null
                )
            } else {
                Banner(
                    container = RemoteColors.ErrorContainer.copy(alpha = 0.5f),
                    content = RemoteColors.OnErrorContainer,
                    leading = null,
                    text = "Connection lost — check PC power & Wi-Fi",
                    actionLabel = "Retry",
                    onAction = onRetry
                )
            }
        }
        is ConnectionUiState.Disconnected -> Banner(
            container = RemoteColors.ErrorContainer.copy(alpha = 0.5f),
            content = RemoteColors.OnErrorContainer,
            leading = null,
            text = "PC unavailable",
            actionLabel = "Connect",
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
    Surface(
        color = container,
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            leading?.invoke()
            Text(
                text,
                style = MaterialTheme.typography.bodySmall.copy(fontWeight = FontWeight.Medium),
                color = content,
                modifier = Modifier.weight(1f).padding(start = if (leading != null) 8.dp else 0.dp)
            )
            if (actionLabel != null && onAction != null) {
                TextButton(
                    onClick = onAction,
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 8.dp)
                ) {
                    Text(actionLabel, color = content, style = MaterialTheme.typography.labelMedium)
                }
            }
        }
    }
}

/**
 * Modern bottom sheet detailing active PC connection, security pin, transport telemetry,
 * and disconnect controls.
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
    androidx.compose.material3.ModalBottomSheet(
        onDismissRequest = onDismiss,
        containerColor = MaterialTheme.colorScheme.surface
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 20.dp)
                .padding(bottom = 32.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            val host = connection.currentHost
            val pcName = host?.let { settingsStore.getName(it) ?: it } ?: "PC"
            val ui = connection.uiState(connState)

            // Header Row
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(44.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .background(MaterialTheme.colorScheme.surfaceContainerHigh),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Outlined.Computer,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(24.dp)
                    )
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = pcName,
                        style = MaterialTheme.typography.titleLarge.copy(fontWeight = FontWeight.SemiBold)
                    )
                    Text(
                        text = host?.let { "$it:${connection.currentPort}" } ?: "Not connected",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            // Connection Telemetry Card
            Surface(
                shape = RoundedCornerShape(14.dp),
                color = MaterialTheme.colorScheme.surfaceContainer,
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(
                    modifier = Modifier.padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    DetailRow(
                        label = "Session Status",
                        value = when (ui) {
                            is ConnectionUiState.Connected -> "Connected"
                            is ConnectionUiState.Connecting -> "Connecting…"
                            is ConnectionUiState.Reconnecting -> "Reconnecting (attempt ${ui.attempt})"
                            is ConnectionUiState.Failed ->
                                if (ui.expectedShutdown) "Shutting down" else "Connection Failed"
                            ConnectionUiState.Disconnected -> "Disconnected"
                        },
                        isStatus = true,
                        statusColor = when (ui) {
                            is ConnectionUiState.Connected -> RemoteColors.Positive
                            is ConnectionUiState.Connecting, is ConnectionUiState.Reconnecting -> RemoteColors.Warning
                            else -> RemoteColors.Error
                        }
                    )
                    HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f))
                    DetailRow("Network Address", host?.let { "$it:${connection.currentPort}" } ?: "—", isMono = true)
                    HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f))
                    DetailRow("Transport Layer", "WebSocket Secure (WSS)")
                    HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f))
                    DetailRow(
                        label = "TLS Certificate",
                        value = if (host != null && pinStore.getPin(host) != null) "Pinned (SHA-256 TOFU)" else "Standard TLS"
                    )
                    HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.2f))
                    DetailRow("Routing", "Direct Local LAN (Zero Cloud)")
                }
            }

            Spacer(modifier = Modifier.height(4.dp))

            // Disconnect Action Button
            OutlinedButton(
                onClick = onDisconnect,
                modifier = Modifier.fillMaxWidth().height(48.dp),
                shape = RoundedCornerShape(12.dp),
                colors = ButtonDefaults.outlinedButtonColors(
                    contentColor = MaterialTheme.colorScheme.error
                ),
                border = ButtonDefaults.outlinedButtonBorder.copy(
                    brush = androidx.compose.ui.graphics.SolidColor(MaterialTheme.colorScheme.error.copy(alpha = 0.5f))
                )
            ) {
                Icon(
                    Icons.Outlined.PowerSettingsNew,
                    contentDescription = null,
                    modifier = Modifier.size(18.dp).padding(end = 8.dp)
                )
                Text("End Session & Disconnect")
            }
        }
    }
}

@Composable
private fun DetailRow(
    label: String,
    value: String,
    isStatus: Boolean = false,
    statusColor: androidx.compose.ui.graphics.Color = MaterialTheme.colorScheme.onSurface,
    isMono: Boolean = false
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        if (isStatus) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .background(statusColor, CircleShape)
                )
                Text(
                    text = value,
                    style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Medium),
                    color = statusColor
                )
            }
        } else {
            Text(
                text = value,
                style = MaterialTheme.typography.bodyMedium.copy(
                    fontFamily = if (isMono) FontFamily.Monospace else FontFamily.Default,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurface
            )
        }
    }
}
