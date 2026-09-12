package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.network.TokenStore
import kotlin.math.roundToInt

/**
 * Settings (02-FEATURE-SPECIFICATION.md F9): paired PCs with Forget,
 * touchpad sensitivity (F2.6/F9.2), and an about note. "Forget" deletes the
 * local token only — true revocation is agent-side and still a tracked gap
 * (09-SECURITY-PRIVACY.md §8).
 */
@Composable
fun SettingsScreen(
    tokenStore: TokenStore,
    settingsStore: SettingsStore,
    onBack: () -> Unit
) {
    var hosts by remember { mutableStateOf(tokenStore.allHosts()) }
    val sensitivity by settingsStore.sensitivity.collectAsState()
    var renameTarget by remember { mutableStateOf<String?>(null) }
    var renameValue by remember { mutableStateOf("") }

    renameTarget?.let { host ->
        AlertDialog(
            onDismissRequest = { renameTarget = null },
            title = { Text("Rename PC") },
            text = {
                OutlinedTextField(
                    value = renameValue,
                    onValueChange = { renameValue = it },
                    label = { Text("Display name") },
                    singleLine = true
                )
            },
            confirmButton = {
                Button(
                    onClick = {
                        settingsStore.setName(host, renameValue)
                        renameTarget = null
                    }
                ) { Text("Save") }
            },
            dismissButton = {
                TextButton(onClick = { renameTarget = null }) { Text("Cancel") }
            }
        )
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text("Settings", style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = onBack) { Text("Back") }
        }

        // --- Paired PCs ---
        Text("Paired PCs", style = MaterialTheme.typography.titleMedium)
        if (hosts.isEmpty()) {
            Text(
                "No PCs paired yet — pair one from the connect screen.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        } else {
            hosts.forEach { host ->
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text(
                        settingsStore.getName(host) ?: host,
                        style = MaterialTheme.typography.bodyLarge
                    )
                    Row {
                        TextButton(
                            onClick = {
                                renameTarget = host
                                renameValue = settingsStore.getName(host) ?: host
                            }
                        ) { Text("Rename") }
                        TextButton(
                            onClick = {
                                tokenStore.forget(host)
                                settingsStore.removeName(host)
                                hosts = tokenStore.allHosts()
                            }
                        ) { Text("Forget") }
                    }
                }
            }
        }

        // --- Touchpad sensitivity ---
        Text("Touchpad sensitivity", style = MaterialTheme.typography.titleMedium)
        Text(
            "Higher = the cursor moves further for the same finger travel.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically
        ) {
            Slider(
                value = sensitivity,
                onValueChange = { settingsStore.setSensitivity(it) },
                valueRange = SettingsStore.SENSITIVITY_MIN..SettingsStore.SENSITIVITY_MAX,
                steps = 24,
                modifier = Modifier.weight(1f)
            )
            Text(
                // One decimal, e.g. "1.5×"
                "${((sensitivity * 10).roundToInt()) / 10f}×",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(start = 12.dp)
            )
        }

        // --- About ---
        Text("About", style = MaterialTheme.typography.titleMedium)
        Text(
            "PC Remote v0.1.0 — controls a Windows PC on your local network. " +
                "No data leaves your LAN.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}