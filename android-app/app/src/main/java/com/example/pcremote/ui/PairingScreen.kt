package com.example.pcremote.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
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
import com.example.pcremote.ui.theme.RemoteColors
import kotlinx.coroutines.delay

/**
 * Production-ready Pairing & Discovery Screen:
 * - Direct local Wi-Fi scan with live discovery status
 * - Fast reconnect for known PCs; clear pairing code flow for new PCs
 * - Structured manual IP connection deck with validation
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
    var portText by remember { mutableStateOf("58642") }
    var pairingCode by remember { mutableStateOf("") }
    var showManual by remember { mutableStateOf(false) }
    val connecting = connState == ConnectionState.CONNECTING

    fun onPcSelected(pc: DiscoveredPc) {
        host = pc.host
        if (tokenStore.getToken(pc.host) != null) {
            // Previously paired: reconnect directly
            connection.connect(host = pc.host, pairingCode = null)
        } else {
            // Unknown host: prefill and prompt for pairing code
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
            .padding(horizontal = 20.dp, vertical = 20.dp),
        verticalArrangement = Arrangement.spacedBy(18.dp)
    ) {
        // App Brand Hero Header
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(48.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    Icons.Outlined.Computer,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(28.dp)
                )
            }
            Column {
                Text(
                    text = "PC Remote",
                    style = MaterialTheme.typography.headlineSmall.copy(fontWeight = FontWeight.Bold)
                )
                Text(
                    text = "Direct encrypted local Wi-Fi control",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }

        // --- DISCOVERY SECTION ---
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text(
                        text = "NEARBY WORKSTATIONS",
                        style = MaterialTheme.typography.labelSmall.copy(
                            letterSpacing = 1.2.sp,
                            fontWeight = FontWeight.SemiBold
                        ),
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    if (snapshot.status == DiscoveryStatus.SEARCHING) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(12.dp),
                            strokeWidth = 2.dp,
                            color = MaterialTheme.colorScheme.primary
                        )
                    }
                }

                IconButton(
                    onClick = { discovery.start() },
                    modifier = Modifier.size(32.dp)
                ) {
                    Icon(
                        Icons.Default.Refresh,
                        contentDescription = "Refresh Discovery",
                        modifier = Modifier.size(18.dp),
                        tint = MaterialTheme.colorScheme.primary
                    )
                }
            }

            when {
                snapshot.status == DiscoveryStatus.FAILED -> {
                    EmptyState(
                        icon = Icons.Outlined.Computer,
                        title = "Discovery scan blocked",
                        body = "mDNS broadcast is restricted on this network. Connect manually below using your PC's IP address.",
                        ctaLabel = "Retry Scan",
                        onCta = { discovery.start() }
                    )
                }
                snapshot.pcs.isEmpty() && showEmpty -> {
                    EmptyState(
                        icon = Icons.Outlined.Wifi,
                        title = "No workstations detected",
                        body = "Ensure the PC Remote Server is running on your Windows PC and both devices share the same local Wi-Fi.",
                        ctaLabel = "Scan Again",
                        onCta = { discovery.start() }
                    )
                }
                snapshot.pcs.isNotEmpty() -> {
                    snapshot.pcs.forEach { pc ->
                        PcCard(
                            pc = pc,
                            displayName = settingsStore.getName(pc.host) ?: pc.displayName,
                            isPaired = tokenStore.getToken(pc.host) != null,
                            onClick = { onPcSelected(pc) }
                        )
                    }
                }
                else -> {
                    Surface(
                        shape = RoundedCornerShape(12.dp),
                        color = MaterialTheme.colorScheme.surfaceContainer,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Row(
                            modifier = Modifier.padding(16.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(12.dp)
                        ) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(18.dp),
                                strokeWidth = 2.dp,
                                color = MaterialTheme.colorScheme.primary
                            )
                            Text(
                                text = "Scanning local subnet for Windows PCs…",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }
            }
        }

        // --- MANUAL CONNECTION SECTION ---
        Surface(
            shape = RoundedCornerShape(16.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { showManual = !showManual },
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(
                            Icons.Default.Add,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp),
                            tint = MaterialTheme.colorScheme.primary
                        )
                        Text(
                            text = "Connect by IP Address",
                            style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.SemiBold)
                        )
                    }
                    Text(
                        text = if (showManual) "Hide" else "Show",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.primary
                    )
                }

                if (showManual) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        OutlinedTextField(
                            value = host,
                            onValueChange = { host = it.trim() },
                            label = { Text("PC IP Address") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                            colors = OutlinedTextFieldDefaults.colors(
                                focusedBorderColor = MaterialTheme.colorScheme.primary,
                                unfocusedBorderColor = MaterialTheme.colorScheme.outline.copy(alpha = 0.4f)
                            ),
                            modifier = Modifier.weight(1f)
                        )

                        OutlinedTextField(
                            value = portText,
                            onValueChange = { portText = it.take(5).filter { c -> c.isDigit() } },
                            label = { Text("Port") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            colors = OutlinedTextFieldDefaults.colors(
                                focusedBorderColor = MaterialTheme.colorScheme.primary,
                                unfocusedBorderColor = MaterialTheme.colorScheme.outline.copy(alpha = 0.4f)
                            ),
                            modifier = Modifier.width(100.dp)
                        )
                    }

                    OutlinedTextField(
                        value = pairingCode,
                        onValueChange = { pairingCode = it.take(6).filter { c -> c.isDigit() } },
                        label = { Text("Pairing Code (6 digits, first time)") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = MaterialTheme.colorScheme.primary,
                            unfocusedBorderColor = MaterialTheme.colorScheme.outline.copy(alpha = 0.4f)
                        ),
                        modifier = Modifier.fillMaxWidth()
                    )

                    Button(
                        onClick = {
                            val parsedPort = portText.toIntOrNull() ?: 58642
                            connection.connect(host = host, port = parsedPort, pairingCode = pairingCode.ifBlank { null })
                        },
                        enabled = host.isNotBlank() && !connecting,
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(50.dp)
                    ) {
                        if (connecting) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(20.dp),
                                strokeWidth = 2.dp,
                                color = MaterialTheme.colorScheme.onPrimary
                            )
                        } else {
                            Text("Connect to Workstation", fontWeight = FontWeight.SemiBold)
                        }
                    }

                    if (connState == ConnectionState.FAILED) {
                        Surface(
                            shape = RoundedCornerShape(8.dp),
                            color = RemoteColors.ErrorContainer.copy(alpha = 0.4f),
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text(
                                text = "Unable to establish connection. Verify the IP address, pairing code, and PC firewall permissions.",
                                style = MaterialTheme.typography.bodySmall,
                                color = RemoteColors.Error,
                                modifier = Modifier.padding(10.dp)
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun <T> kotlinx.coroutines.flow.StateFlow<T>.collectAsStateSafe() =
    androidx.compose.runtime.produceState(initialValue = this.value, this) {
        this@collectAsStateSafe.collect { value = it }
    }
