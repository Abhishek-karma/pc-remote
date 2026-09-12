package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.network.TokenStore
import com.example.pcremote.ui.components.SectionHeader
import kotlin.math.roundToInt

/**
 * Settings (02 F9): semantic sections — trusted PCs, touchpad behavior, about.
 * "Forget" deletes the local token; true revocation is agent-side and tracked
 * separately (09-SECURITY-PRIVACY.md §8).
 */
@Composable
fun SettingsScreen(
    tokenStore: TokenStore,
    settingsStore: SettingsStore,
    connection: RemoteConnection,
    onBack: () -> Unit,
    modifier: Modifier = Modifier
) {
    var hosts by remember { mutableStateOf(tokenStore.allHosts()) }
    val sensitivity by settingsStore.sensitivity.collectAsState()
    val haptics by settingsStore.hapticsEnabled.collectAsState()
    var renameTarget by remember { mutableStateOf<String?>(null) }
    var renameValue by remember { mutableStateOf("") }

    Column(
        modifier = modifier
            .fillMaxSize()
            .statusBarsPadding()
            .navigationBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("Settings", style = MaterialTheme.typography.titleLarge)
            TextButton(onClick = onBack) { Text("Done") }
        }

        SectionHeader("Trusted PCs")
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
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            settingsStore.getName(host) ?: host,
                            style = MaterialTheme.typography.bodyLarge
                        )
                        Text(
                            host,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
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

        SectionHeader("Touchpad")
        Text(
            "Sensitivity",
            style = MaterialTheme.typography.bodyLarge,
            modifier = Modifier.padding(top = 4.dp)
        )
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Slider(
                value = sensitivity,
                onValueChange = { settingsStore.setSensitivity(it) },
                valueRange = SettingsStore.SENSITIVITY_MIN..SettingsStore.SENSITIVITY_MAX,
                steps = 24,
                modifier = Modifier.weight(1f)
            )
            Text(
                "${((sensitivity * 10).roundToInt()) / 10f}\u00D7",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(start = 12.dp)
            )
        }
        SwitchRow(
            label = "Haptic feedback",
            description = "Subtle vibration on clicks and D-pad",
            checked = haptics,
            onCheckedChange = { settingsStore.setHapticsEnabled(it) }
        )

        SectionHeader("About")
        Text(
            "PC Remote v0.1.0 — controls a Windows PC on your local network " +
                "over WSS. No data leaves your LAN.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Text(
            connection.currentHost?.let { "Connected to ${connection.currentHost}:${connection.currentPort}" }
                ?: "Not connected",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }

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
}

@Composable
private fun SwitchRow(
    label: String,
    description: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .padding(top = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(label, style = MaterialTheme.typography.bodyLarge)
            Text(
                description,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        Switch(checked = checked, onCheckedChange = onCheckedChange)
    }
}