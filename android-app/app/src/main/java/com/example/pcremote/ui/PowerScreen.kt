package com.example.pcremote.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.Shield
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.theme.RemoteColors

private enum class PowerAction(
    val label: String,
    val action: String,
    val isDestructive: Boolean,
    val description: String
) {
    LOCK("Lock Session", "lock", false, "Locks the desktop immediately. Apps stay open."),
    SLEEP("Sleep Mode", "sleep", false, "Puts PC into low-power sleep state."),
    RESTART("Restart PC", "restart", true, "Reboots the operating system. Reconnect required."),
    SHUTDOWN("Shut Down PC", "shutdown", true, "Powers off the PC completely. Session will end.")
}

/**
 * Authoritative System Power Management:
 * - Clear distinction between Non-destructive (Safe) and Destructive operations
 * - Semantic coloring (Cyan/Neutral for safe; Crimson for destructive)
 * - Confirmation modal with explicit impact warning
 * - Compact, purposeful layout without empty space
 */
@Composable
fun PowerScreen(
    connection: RemoteConnection,
    pcName: String,
    onFeedback: (String) -> Unit
) {
    var pendingAction by remember { mutableStateOf<PowerAction?>(null) }
    val haptics = LocalHapticFeedback.current

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(18.dp)
    ) {
        // Active Target PC Banner
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Row(
                modifier = Modifier.padding(16.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(42.dp)
                        .clip(RoundedCornerShape(10.dp))
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
                        text = "Target System: $pcName",
                        style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.SemiBold)
                    )
                    Text(
                        text = "Commands dispatch over direct encrypted LAN link",
                        style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        }

        // --- 1. SAFE POWER ACTIONS SECTION ---
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Icon(
                    Icons.Outlined.Shield,
                    contentDescription = null,
                    tint = RemoteColors.Positive,
                    modifier = Modifier.size(16.dp)
                )
                Text(
                    text = "SAFE OPERATIONS",
                    style = MaterialTheme.typography.labelSmall.copy(
                        letterSpacing = 1.2.sp,
                        fontWeight = FontWeight.SemiBold
                    ),
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            // Lock Workstation Card
            SafeActionCard(
                title = "Lock Workstation",
                subtitle = "Locks the Windows session immediately. Running applications and background tasks remain active.",
                icon = Icons.Filled.Lock,
                accentColor = MaterialTheme.colorScheme.primary,
                onClick = {
                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                    connection.sendPower("lock")
                    onFeedback("Sent Lock command to $pcName")
                }
            )

            // Sleep Mode Card
            SafeActionCard(
                title = "Put PC to Sleep",
                subtitle = "Places the computer into energy-saving sleep state. Can be woken up remotely or via keyboard.",
                icon = Icons.Filled.Bedtime,
                accentColor = RemoteColors.Warning,
                onClick = {
                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                    connection.sendPower("sleep")
                    onFeedback("Sent Sleep command to $pcName")
                }
            )
        }

        // --- 2. DESTRUCTIVE ACTIONS SECTION ---
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Icon(
                    Icons.Filled.Warning,
                    contentDescription = null,
                    tint = RemoteColors.Error,
                    modifier = Modifier.size(16.dp)
                )
                Text(
                    text = "DESTRUCTIVE OPERATIONS (REQUIRES CONFIRMATION)",
                    style = MaterialTheme.typography.labelSmall.copy(
                        letterSpacing = 1.2.sp,
                        fontWeight = FontWeight.SemiBold
                    ),
                    color = RemoteColors.Error
                )
            }

            // Restart Card
            DestructiveActionCard(
                title = "Restart Computer",
                subtitle = "Reboots the PC operating system. Active remote session will disconnect.",
                icon = Icons.Filled.RestartAlt,
                onClick = {
                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                    pendingAction = PowerAction.RESTART
                }
            )

            // Shut Down Card
            DestructiveActionCard(
                title = "Power Off / Shut Down",
                subtitle = "Performs a clean system shutdown. PC must be manually powered on afterward.",
                icon = Icons.Filled.PowerSettingsNew,
                onClick = {
                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                    pendingAction = PowerAction.SHUTDOWN
                }
            )
        }
    }

    // Safety Confirmation Modal
    pendingAction?.let { action ->
        AlertDialog(
            onDismissRequest = { pendingAction = null },
            icon = {
                Box(
                    modifier = Modifier
                        .size(48.dp)
                        .background(RemoteColors.ErrorContainer.copy(alpha = 0.5f), CircleShape),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Filled.Warning,
                        contentDescription = null,
                        tint = RemoteColors.Error,
                        modifier = Modifier.size(26.dp)
                    )
                }
            },
            title = {
                Text(
                    text = "${action.label}?",
                    style = MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.Bold)
                )
            },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        text = if (action == PowerAction.SHUTDOWN) {
                            "Are you sure you want to shut down $pcName? Any unsaved documents or applications will be closed, and this remote session will immediately terminate."
                        } else {
                            "Are you sure you want to restart $pcName? The system will close running apps and reboot. You will need to reconnect after Windows restarts."
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        connection.sendPower(action.action)
                        onFeedback(
                            if (action == PowerAction.SHUTDOWN) "Shutting down $pcName…"
                            else "Restarting $pcName…"
                        )
                        pendingAction = null
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = RemoteColors.Error,
                        contentColor = Color.White
                    ),
                    shape = RoundedCornerShape(10.dp)
                ) {
                    Text("Confirm ${action.label}", fontWeight = FontWeight.SemiBold)
                }
            },
            dismissButton = {
                TextButton(
                    onClick = { pendingAction = null }
                ) {
                    Text("Cancel")
                }
            },
            containerColor = MaterialTheme.colorScheme.surface
        )
    }
}

@Composable
private fun SafeActionCard(
    title: String,
    subtitle: String,
    icon: ImageVector,
    accentColor: Color,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.surfaceContainer,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(accentColor.copy(alpha = 0.15f)),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = icon,
                    contentDescription = null,
                    tint = accentColor,
                    modifier = Modifier.size(24.dp)
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.SemiBold)
                )
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall.copy(fontSize = 12.sp),
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

@Composable
private fun DestructiveActionCard(
    title: String,
    subtitle: String,
    icon: ImageVector,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.surfaceContainer,
        border = androidx.compose.foundation.BorderStroke(1.dp, RemoteColors.Error.copy(alpha = 0.4f)),
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(RemoteColors.ErrorContainer.copy(alpha = 0.4f)),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = icon,
                    contentDescription = null,
                    tint = RemoteColors.Error,
                    modifier = Modifier.size(24.dp)
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    style = MaterialTheme.typography.titleSmall.copy(
                        fontWeight = FontWeight.SemiBold,
                        color = RemoteColors.Error
                    )
                )
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall.copy(fontSize = 12.sp),
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}
