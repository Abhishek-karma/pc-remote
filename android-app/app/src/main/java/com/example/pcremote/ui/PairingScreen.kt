package com.example.pcremote.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.ConnectionState
import com.example.pcremote.network.DiscoveredPc
import com.example.pcremote.network.DiscoveryService
import com.example.pcremote.network.DiscoveryStatus
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.TokenStore
import kotlinx.coroutines.delay

/**
 * First-run / connect screen. An mDNS browse runs while this screen is visible
 * and lists discovered PCs ("Discover nearby PC"); tapping a PC prefills the
 * IP field, or connects directly if a trust token is already saved for that
 * host. The manual IP + pairing-code fields remain as the fallback (and as the
 * only path when discovery is blocked or fails). Once paired, RemoteConnection
 * saves a token so this screen can be skipped on future launches for the same PC.
 */
@Composable
fun PairingScreen(
    connection: RemoteConnection,
    tokenStore: TokenStore,
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

    // NSD keeps listening indefinitely; after a grace period with no results,
    // surface the "no PCs found" hint while the browse keeps running.
    var emptyHint by remember { mutableStateOf(false) }
    LaunchedEffect(snapshot.status) {
        if (snapshot.status == DiscoveryStatus.SEARCHING) {
            emptyHint = false
            delay(5_000)
            if (snapshot.status == DiscoveryStatus.SEARCHING && snapshot.pcs.isEmpty()) {
                emptyHint = true
            }
        }
    }

    var host by remember { mutableStateOf("") }
    var pairingCode by remember { mutableStateOf("") }
    val state by connection.state.collectAsStateSafe()

    fun onPcSelected(pc: DiscoveredPc) {
        host = pc.host
        // Never auto-pair with a discovered host; connect directly only when
        // this host was already paired (a saved token exists for it).
        if (tokenStore.getToken(pc.host) != null) {
            connection.connect(host = pc.host, pairingCode = null)
        }
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Text("Connect to your PC", style = MaterialTheme.typography.titleLarge)
        Text(
            "Run the PC Remote agent on your Windows PC, then pick it from the list " +
                "below or enter the IP and pairing code shown in its console window.",
            style = MaterialTheme.typography.bodyMedium
        )

        // --- Discovery section ---
        Text("Discover nearby PC", style = MaterialTheme.typography.titleMedium)
        when (snapshot.status) {
            DiscoveryStatus.SEARCHING -> Text(
                "Looking for PCs…",
                style = MaterialTheme.typography.bodyMedium
            )
            DiscoveryStatus.FAILED -> Text(
                "Discovery failed — enter the IP manually below.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error
            )
            else -> {}
        }
        snapshot.pcs.forEach { pc ->
            Surface(
                shape = MaterialTheme.shapes.medium,
                tonalElevation = 1.dp,
                modifier = Modifier.fillMaxWidth().clickable { onPcSelected(pc) }
            ) {
                Column(Modifier.padding(12.dp)) {
                    Text(pc.displayName, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "${pc.host}:${pc.port}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        }
        if (emptyHint && snapshot.pcs.isEmpty()) {
            Text(
                "No PCs found — make sure both devices are on the same Wi-Fi " +
                    "and the agent is running.",
                style = MaterialTheme.typography.bodyMedium
            )
        }
        TextButton(onClick = { discovery.start() }) {
            Text("Refresh")
        }

        // Push the manual-entry block + Connect toward the bottom of the
        // screen so a tall display doesn't leave a dead void under a floating
        // form anchored to the top.
        Spacer(modifier = Modifier.weight(1f))

        // --- Manual fallback ---
        OutlinedTextField(
            value = host,
            onValueChange = { host = it },
            label = { Text("PC IP address") },
            modifier = Modifier.fillMaxWidth()
        )

        OutlinedTextField(
            value = pairingCode,
            onValueChange = { pairingCode = it },
            label = { Text("Pairing code (first time only)") },
            modifier = Modifier.fillMaxWidth()
        )

        Button(
            onClick = { connection.connect(host = host, pairingCode = pairingCode.ifBlank { null }) },
            modifier = Modifier.fillMaxWidth()
        ) {
            Text("Connect")
        }

        when (state) {
            ConnectionState.CONNECTING -> Text("Connecting…")
            ConnectionState.CONNECTED -> {
                Text("Connected!", color = MaterialTheme.colorScheme.primary)
                onConnected()
            }
            ConnectionState.FAILED -> Text(
                "Couldn't connect — check the IP/pairing code and that the agent is running.",
                color = MaterialTheme.colorScheme.error
            )
            else -> {}
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