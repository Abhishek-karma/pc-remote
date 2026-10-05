package com.example.pcremote.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.example.pcremote.discovery.DiscoveredPc
import com.example.pcremote.discovery.DiscoveryStatus

/**
 * The first screen: pick a PC.
 *
 * Discovered PCs are listed and tappable. Tapping one asks for the six-digit code
 * shown on the PC, which is the only manual step in the whole product. Manual IP
 * entry sits underneath for the case discovery cannot cover.
 */
@Composable
fun PcListScreen(
    pcs: List<DiscoveredPc>,
    status: DiscoveryStatus?,
    onSelect: (DiscoveredPc, String) -> Unit,
    onManual: (String, String) -> Unit,
    onRetryDiscovery: () -> Unit,
) {
    var manualIp by remember { mutableStateOf("") }

    // The PC we are pairing with, held while the code is typed.
    var pairingWith by remember { mutableStateOf<Pair<String, String>?>(null) }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Text(
            text = "Nearby PCs",
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(bottom = 12.dp),
        )

        when {
            pcs.isNotEmpty() -> LazyColumn(
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.weight(1f),
            ) {
                items(pcs, key = { it.serviceName }) { pc ->
                    PcRow(pc.name, pc.host) { pairingWith = pc.host to pc.name }
                }
            }

            status == DiscoveryStatus.FAILED -> Text(
                text = "Could not search the network. Make sure the phone and PC are on " +
                    "the same Wi-Fi, and that the network is set to Private on the PC.",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.weight(1f).padding(vertical = 8.dp),
            )

            status == DiscoveryStatus.SEARCHING -> Text(
                text = "Searching…",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.weight(1f).padding(vertical = 8.dp),
            )

            else -> Text(
                text = "No PCs found yet.",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.weight(1f).padding(vertical = 8.dp),
            )
        }

        OutlinedButton(onClick = onRetryDiscovery, modifier = Modifier.padding(bottom = 16.dp)) {
            Text("Search again")
        }

        Text(
            text = "Connect by IP",
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(bottom = 4.dp),
        )
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            OutlinedTextField(
                value = manualIp,
                onValueChange = { manualIp = it },
                label = { Text("IP address") },
                singleLine = true,
                modifier = Modifier.weight(1f),
            )
            Button(
                onClick = {
                    manualIp.trim().takeIf { it.isNotEmpty() }
                        ?.let { pairingWith = it to it }
                },
                enabled = manualIp.isNotBlank(),
                modifier = Modifier.padding(start = 8.dp),
            ) {
                Text("Go")
            }
        }
    }

    pairingWith?.let { (host, name) ->
        PairingDialog(
            pcName = name,
            onDismiss = { pairingWith = null },
            onConfirm = { code ->
                pairingWith = null
                onManual(host, code)
            },
        )
    }
}

/** Asks for the code shown on the PC. Digits only: it is always six digits. */
@Composable
private fun PairingDialog(
    pcName: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var code by remember { mutableStateOf("") }
    val valid = code.length == 6

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pair with $pcName") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    text = "Open PC Remote on the computer to see its pairing code, " +
                        "then type it here.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                OutlinedTextField(
                    value = code,
                    onValueChange = { typed ->
                        code = typed.filter { it.isDigit() }.take(6)
                    },
                    label = { Text("Pairing code") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(code) }, enabled = valid) { Text("Connect") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel") }
        },
    )
}

@Composable
private fun PcRow(name: String, host: String, onClick: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(text = name, style = MaterialTheme.typography.bodyLarge)
        Text(
            text = host,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}