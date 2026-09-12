package com.example.pcremote.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.theme.RemoteColors

/**
 * Single UI-facing connection state, derived from the one authoritative
 * source (RemoteConnection.state) — the shell and screens never keep their
 * own "connected" boolean.
 */
sealed interface ConnectionUiState {
    data object Disconnected : ConnectionUiState
    data object Connecting : ConnectionUiState
    data class Connected(val host: String) : ConnectionUiState
    data class Reconnecting(val attempt: Int) : ConnectionUiState
    data class Failed(val expectedShutdown: Boolean) : ConnectionUiState
}

fun RemoteConnection.uiState(state: ConnectionState): ConnectionUiState = when (state) {
    ConnectionState.CONNECTED -> ConnectionUiState.Connected(currentHost ?: "")
    ConnectionState.RECONNECTING -> ConnectionUiState.Reconnecting(reconnectAttempt)
    ConnectionState.CONNECTING, ConnectionState.AWAITING_PAIRING -> ConnectionUiState.Connecting
    ConnectionState.FAILED -> ConnectionUiState.Failed(lastDisconnectExpected)
    ConnectionState.DISCONNECTED ->
        if (lastDisconnectExpected) ConnectionUiState.Failed(expectedShutdown = true)
        else ConnectionUiState.Disconnected
}

/** ●/◌/○ status dot. Color is paired with a text label — never color alone. */
@Composable
fun StatusDot(color: Color, modifier: Modifier = Modifier, description: String) {
    Box(
        modifier = modifier
            .size(8.dp)
            .background(color, CircleShape)
            .semantics { contentDescription = description }
    )
}

@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text.uppercase(),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier.padding(top = 8.dp, bottom = 4.dp)
    )
}

/**
 * Intentional empty state: what happened, why, what to do next.
 * All three parts are required by the caller.
 */
@Composable
fun EmptyState(
    icon: ImageVector,
    title: String,
    body: String,
    modifier: Modifier = Modifier,
    ctaLabel: String? = null,
    onCta: (() -> Unit)? = null
) {
    Column(
        modifier = modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(36.dp).padding(bottom = 4.dp)
        )
        Text(title, style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
        Text(
            body,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center
        )
        if (ctaLabel != null && onCta != null) {
            TextButton(onClick = onCta) { Text(ctaLabel) }
        }
    }
}

/** Compact icon button with guaranteed content description (13 §2). */
@Composable
fun RemoteIconButton(
    icon: ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    tint: Color = MaterialTheme.colorScheme.onSurfaceVariant
) {
    Surface(
        onClick = onClick,
        shape = MaterialTheme.shapes.small,
        color = Color.Transparent,
        modifier = modifier.size(48.dp)
    ) {
        Box(contentAlignment = Alignment.Center) {
            Icon(icon, contentDescription = contentDescription, tint = tint, modifier = Modifier.size(24.dp))
        }
    }
}

/** Status colors used by the indicator + banner (single source). */
object StatusColors {
    @Composable
    fun positive() = RemoteColors.Positive
    @Composable
    fun pending() = MaterialTheme.colorScheme.onSurfaceVariant
    @Composable
    fun error() = MaterialTheme.colorScheme.error
}