package com.example.pcremote.ui.components

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Devices
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.theme.RemoteColors

/**
 * Precision Top App Bar:
 * - Friendly PC name is the primary identity
 * - Secondary IP/protocol line
 * - Interactive status badge with animated pulse on connecting
 * - Quick access to PC Switcher and Settings
 * - Minimal vertical height (conserves touchpad space)
 */
@Composable
fun RemoteTopBar(
    title: String,
    connection: RemoteConnection,
    state: ConnectionState,
    onStatusClick: () -> Unit,
    onSettingsClick: () -> Unit,
    onSwitchPcClick: () -> Unit = onStatusClick
) {
    val ui = connection.uiState(state)
    val isConnected = ui is ConnectionUiState.Connected
    val isPending = ui is ConnectionUiState.Connecting || ui is ConnectionUiState.Reconnecting
    val isFailed = ui is ConnectionUiState.Failed || ui is ConnectionUiState.Disconnected

    val statusLabel = when (ui) {
        is ConnectionUiState.Connected -> "Connected"
        is ConnectionUiState.Connecting -> "Connecting…"
        is ConnectionUiState.Reconnecting ->
            if (ui.attempt > 1) "Reconnecting (${ui.attempt})…" else "Reconnecting…"
        is ConnectionUiState.Failed ->
            if (ui.expectedShutdown) "Shutting down" else "Offline"
        ConnectionUiState.Disconnected -> "Disconnected"
    }

    val statusColor = when {
        isConnected -> RemoteColors.Positive
        isPending -> RemoteColors.Warning
        else -> RemoteColors.Error
    }

    val statusBgColor = when {
        isConnected -> RemoteColors.Positive.copy(alpha = 0.12f)
        isPending -> RemoteColors.Warning.copy(alpha = 0.12f)
        else -> RemoteColors.Error.copy(alpha = 0.12f)
    }

    // Subtle pulsing animation when connecting/reconnecting
    val infiniteTransition = rememberInfiniteTransition(label = "pulse")
    val pulseAlpha by infiniteTransition.animateFloat(
        initialValue = 0.4f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(800),
            repeatMode = RepeatMode.Reverse
        ),
        label = "dotAlpha"
    )

    Surface(
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(start = 16.dp, end = 8.dp, top = 6.dp, bottom = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            // Left & Center: Primary Device Identity & Status Badge
            Column(
                modifier = Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(8.dp))
                    .clickable(onClick = onStatusClick)
                    .padding(vertical = 2.dp)
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text(
                        text = title,
                        style = MaterialTheme.typography.titleMedium.copy(
                            fontWeight = FontWeight.SemiBold,
                            letterSpacing = (-0.2).sp
                        ),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        color = MaterialTheme.colorScheme.onSurface
                    )

                    // Status Pill Badge
                    Row(
                        modifier = Modifier
                            .clip(RoundedCornerShape(12.dp))
                            .background(statusBgColor)
                            .padding(horizontal = 7.dp, vertical = 2.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(5.dp)
                    ) {
                        Box(
                            modifier = Modifier
                                .size(6.dp)
                                .alpha(if (isPending) pulseAlpha else 1f)
                                .background(statusColor, CircleShape)
                                .semantics { contentDescription = statusLabel }
                        )
                        Text(
                            text = statusLabel,
                            style = MaterialTheme.typography.labelSmall.copy(
                                fontWeight = FontWeight.Medium,
                                fontSize = 11.sp
                            ),
                            color = statusColor
                        )
                    }
                }

                // Secondary metadata: IP address & encrypted transport
                val hostInfo = connection.currentHost
                val subtitle = if (hostInfo != null) {
                    "$hostInfo · WSS Encrypted"
                } else {
                    "Local Network PC"
                }
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }

            Spacer(modifier = Modifier.width(8.dp))

            // Trailing actions: Switch PC & Settings
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconButton(
                    onClick = onSwitchPcClick,
                    modifier = Modifier.size(40.dp)
                ) {
                    Icon(
                        imageVector = Icons.Outlined.Devices,
                        contentDescription = "Switch PC",
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.size(20.dp)
                    )
                }

                IconButton(
                    onClick = onSettingsClick,
                    modifier = Modifier.size(40.dp)
                ) {
                    Icon(
                        imageVector = Icons.Outlined.Settings,
                        contentDescription = "Settings",
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.size(20.dp)
                    )
                }
            }
        }
    }
}

