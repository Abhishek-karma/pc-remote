package com.example.pcremote.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.DiscoveredPc
import com.example.pcremote.network.DiscoveryService
import com.example.pcremote.network.DiscoveryStatus
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.network.TokenStore
import com.example.pcremote.ui.components.EmptyState
import com.example.pcremote.ui.components.PcCard
import com.example.pcremote.ui.components.SectionHeader
import com.example.pcremote.ui.theme.Spacing
import com.example.pcremote.ui.theme.TouchTarget
import kotlinx.coroutines.delay

/**
 * Pairing screen (04 §8): discovery list of PC cards with explicit states
 * (searching / found / empty / failed), manual entry as a secondary section,
 * and validation. Security behavior unchanged: a never-paired discovered PC
 * only prefills the form — pairing always requires the code (09 §3).
 */
@Composable
fun PairingScreen(
    connection: RemoteConnection,
    tokenStore: TokenStore,
    settingsStore: SettingsStore,
    pinStore: PinStore,
    connState: ConnectionState,
    onConnected: () -> Unit,
    discoveryProvider: ((android.content.Context) -> DiscoveryService)? = null
) {
    val context = LocalContext.current
    val discovery = remember {
        discoveryProvider?.invoke(context) ?: DiscoveryService.create(context)
    }
    DisposableEffect(Unit) {
        discovery.start()
        onDispose { discovery.stop() }
    }

    val snapshot by discovery.snapshot.collectAsStateSafe()

    // Discovery keeps listening; after a grace period with no results, surface
    // the empty state while the browse continues in the background.
    var showEmpty by remember { mutableStateOf(false) }
    LaunchedEffect(snapshot.status) {
        if (snapshot.status == DiscoveryStatus.SEARCHING) {
            showEmpty = false
            delay(5_000)
            if (snapshot.status == DiscoveryStatus.SEARCHING && snapshot.pcs.isEmpty()) {
                showEmpty = true
            }
        }
    }

    var host by remember { mutableStateOf("") }
    var pairingCode by remember { mutableStateOf("") }
    var showManual by remember { mutableStateOf(false) }
    val connecting = connState == ConnectionState.CONNECTING

    fun onPcSelected(pc: DiscoveredPc) {
        host = pc.host
        if (tokenStore.getToken(pc.host) != null) {
            // Previously paired: reconnect directly, no code prompt (09 §3).
            connection.connect(host = pc.host, pairingCode = null)
        } else {
            // Unknown host: only prefill — pairing still needs the code.
            pairingCode = ""
            showManual = true
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .statusBarsPadding()
            .navigationBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = Spacing.xxl, vertical = Spacing.xl),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg)
    ) {
        Text("Connect to your PC", style = MaterialTheme.typography.headlineSmall)
        Text(
            "Make sure PC Remote is running on your Windows PC.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )

        // --- Discovery ---
        SectionHeader("Your PCs")
        when {
            snapshot.status == DiscoveryStatus.FAILED -> EmptyState(
                icon = Icons.Outlined.Computer,
                title = "Couldn't scan for PCs",
                body = "Discovery is blocked on this network. You can still connect manually.",
                ctaLabel = "Try again",
                onCta = { discovery.start() }
            )
            snapshot.pcs.isEmpty() && showEmpty -> EmptyState(
                icon = Icons.Outlined.Computer,
                title = "No PCs found yet",
                body = "Make sure:\n\u2022 PC Remote is running on the PC\n\u2022 Both devices are on the same network",
                ctaLabel = "Scan again",
                onCta = { discovery.start() }
            )
            else -> {
                Row(
                    verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(Spacing.sm)
                ) {
                    if (snapshot.status == DiscoveryStatus.SEARCHING) {
                        CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
                        Text(
                            "Finding PCs nearby…",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
                snapshot.pcs.forEach { pc ->
                    PcCard(
                        pc = pc,
                        displayName = settingsStore.getName(pc.host) ?: pc.displayName,
                        isPaired = tokenStore.getToken(pc.host) != null,
                        onClick = { onPcSelected(pc) }
                    )
                }
            }
        }

        // --- Manual connection (secondary) ---
        if (!showManual) {
            TextButton(onClick = { showManual = true }) { Text("Connect manually") }
        } else {
            SectionHeader("Manual connection")
            OutlinedTextField(
                value = host,
                onValueChange = { host = it.trim() },
                label = { Text("PC address") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth()
            )
            OutlinedTextField(
                value = pairingCode,
                onValueChange = { pairingCode = it.take(6).filter { c -> c.isDigit() } },
                label = { Text("Pairing code (first time only)") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                modifier = Modifier.fillMaxWidth()
            )
            Button(
                onClick = {
                    connection.connect(host = host, pairingCode = pairingCode.ifBlank { null })
                },
                enabled = host.isNotBlank() && !connecting,
                modifier = Modifier.fillMaxWidth().heightIn(min = TouchTarget.control)
            ) {
                if (connecting) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(18.dp),
                        strokeWidth = 2.dp,
                        color = MaterialTheme.colorScheme.onPrimary
                    )
                } else {
                    Text("Connect")
                }
            }
            if (connState == ConnectionState.FAILED) {
                Text(
                    "Couldn't connect — check the address and pairing code.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            }
        }

    }
}

// Small helper so this file compiles standalone; in the real project just
// import androidx.compose.runtime.collectAsState from androidx.lifecycle-runtime-compose.
@Composable
private fun <T> kotlinx.coroutines.flow.StateFlow<T>.collectAsStateSafe() =
    androidx.compose.runtime.produceState(initialValue = this.value, this) {
        this@collectAsStateSafe.collect { value = it }
    }