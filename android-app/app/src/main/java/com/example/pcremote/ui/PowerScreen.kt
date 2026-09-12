package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection

private enum class PowerAction(val label: String, val message: String) {
    SLEEP("Sleep", "sleep"),        // immediate — no confirmation (FR5.3)
    LOCK("Lock", "lock"),           // immediate — no confirmation (FR5.3)
    RESTART("Restart", "restart"),  // destructive — confirmation required (FR5.2)
    SHUTDOWN("Shut Down", "shutdown")
}

/**
 * Power screen (02-FEATURE-SPECIFICATION.md F5). Sleep and Lock act
 * immediately; Restart and Shut Down require a standard AlertDialog first
 * (required for destructive actions by F5.2 and for TalkBack accessibility
 * by 13-ACCESSIBILITY.md §2).
 */
@Composable
fun PowerScreen(connection: RemoteConnection) {
    // The action awaiting confirmation, null when no dialog is shown.
    var pending by remember { mutableStateOf<PowerAction?>(null) }

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Text("Power", style = MaterialTheme.typography.titleMedium)

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Button(
                onClick = { connection.sendPower("sleep") },
                modifier = Modifier.weight(1f).heightIn(min = 72.dp)
            ) { Text("Sleep") }
            Button(
                onClick = { connection.sendPower("lock") },
                modifier = Modifier.weight(1f).heightIn(min = 72.dp)
            ) { Text("Lock") }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            OutlinedButton(
                onClick = { pending = PowerAction.RESTART },
                modifier = Modifier.weight(1f).heightIn(min = 72.dp)
            ) { Text("Restart") }
            OutlinedButton(
                onClick = { pending = PowerAction.SHUTDOWN },
                modifier = Modifier.weight(1f).heightIn(min = 72.dp)
            ) { Text("Shut Down") }
        }
    }

    pending?.let { action ->
        AlertDialog(
            onDismissRequest = { pending = null },
            title = { Text("${action.label} PC?") },
            text = {
                Text(
                    "This will ${action.message} the PC immediately." +
                        (if (action == PowerAction.SHUTDOWN)
                            " Any unsaved work on the PC will be lost." else "")
                )
            },
            confirmButton = {
                Button(
                    onClick = {
                        connection.sendPower(action.message)
                        pending = null
                    }
                ) { Text(action.label) }
            },
            dismissButton = {
                TextButton(onClick = { pending = null }) { Text("Cancel") }
            }
        )
    }
}