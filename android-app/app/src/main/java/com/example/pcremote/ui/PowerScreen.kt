package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.components.SectionHeader

private enum class PowerAction(val label: String, val action: String) {
    SLEEP("Sleep", "sleep"),          // immediate — no confirmation (FR5.3)
    LOCK("Lock", "lock"),             // immediate — no confirmation (FR5.3)
    RESTART("Restart", "restart"),    // destructive — confirmation required (FR5.2)
    SHUTDOWN("Shut Down", "shutdown")
}

/**
 * Power screen (02 F5): safe actions immediate, destructive actions visually
 * separated and confirmed via standard AlertDialog (accessibility — 13 §2).
 */
@Composable
fun PowerScreen(
    connection: RemoteConnection,
    pcName: String,
    onFeedback: (String) -> Unit
) {
    var pending by remember { mutableStateOf<PowerAction?>(null) }

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        SectionHeader("Safe")
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            SafePowerButton("Lock", Icons.Filled.Lock, Modifier.weight(1f)) {
                connection.sendPower("lock")
                onFeedback("Locking PC…")
            }
            SafePowerButton("Sleep", Icons.Filled.Bedtime, Modifier.weight(1f)) {
                connection.sendPower("sleep")
                onFeedback("Putting PC to sleep…")
            }
        }

        SectionHeader("Destructive — requires confirmation")
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            DestructivePowerButton("Restart", Icons.Filled.RestartAlt, Modifier.weight(1f)) {
                pending = PowerAction.RESTART
            }
            DestructivePowerButton("Shut Down", Icons.Filled.PowerSettingsNew, Modifier.weight(1f)) {
                pending = PowerAction.SHUTDOWN
            }
        }
    }

    pending?.let { action ->
        AlertDialog(
            onDismissRequest = { pending = null },
            title = { Text("${action.label} PC?") },
            text = {
                Text(
                    "$pcName will ${action.action} immediately" +
                        if (action == PowerAction.SHUTDOWN)
                            " and your remote session will end. Unsaved work on the PC will be lost."
                        else ". Your remote session will end."
                )
            },
            confirmButton = {
                Button(
                    onClick = {
                        connection.sendPower(action.action)
                        onFeedback(
                            if (action == PowerAction.SHUTDOWN) "Shutting down PC…"
                            else "Restarting PC…"
                        )
                        pending = null
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.error,
                        contentColor = MaterialTheme.colorScheme.onError
                    )
                ) { Text(action.label) }
            },
            dismissButton = {
                TextButton(onClick = { pending = null }) { Text("Cancel") }
            }
        )
    }
}

@Composable
private fun SafePowerButton(label: String, icon: androidx.compose.ui.graphics.vector.ImageVector, modifier: Modifier, onClick: () -> Unit) {
    OutlinedButton(onClick = onClick, modifier = modifier.height(64.dp)) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Icon(icon, contentDescription = null, modifier = Modifier.height(20.dp))
            Text(label)
        }
    }
}

@Composable
private fun DestructivePowerButton(label: String, icon: androidx.compose.ui.graphics.vector.ImageVector, modifier: Modifier, onClick: () -> Unit) {
    OutlinedButton(
        onClick = onClick,
        modifier = modifier.height(64.dp),
        colors = ButtonDefaults.outlinedButtonColors(
            contentColor = MaterialTheme.colorScheme.error
        )
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Icon(icon, contentDescription = null, modifier = Modifier.height(20.dp))
            Text(label)
        }
    }
}