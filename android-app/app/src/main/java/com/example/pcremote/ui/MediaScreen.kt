package com.example.pcremote.ui

import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.VolumeDown
import androidx.compose.material.icons.filled.VolumeMute
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedIconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import kotlinx.coroutines.delay

/**
 * Media remote (02 F4): transport + press-and-hold volume steps (FR4.3,
 * 150ms repeat). Icons carry content descriptions; labels sit under them.
 */
@Composable
fun MediaScreen(connection: RemoteConnection) {
    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(24.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            MediaButton(
                icon = Icons.Filled.SkipPrevious,
                label = "Previous",
                modifier = Modifier.weight(1f).height(72.dp)
            ) { connection.sendMedia("prev") }
            FilledIconButton(
                onClick = { connection.sendMedia("play_pause") },
                modifier = Modifier.weight(1.3f).height(72.dp)
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = "Play or pause")
                Text("  Play", style = MaterialTheme.typography.labelLarge)
            }
            MediaButton(
                icon = Icons.Filled.SkipNext,
                label = "Next",
                modifier = Modifier.weight(1f).height(72.dp)
            ) { connection.sendMedia("next") }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            HoldRepeatButton(
                icon = Icons.Filled.VolumeDown,
                label = "Volume down",
                modifier = Modifier.weight(1f).height(64.dp)
            ) { connection.sendMedia("vol_down") }
            OutlinedIconButton(
                onClick = { connection.sendMedia("mute") },
                modifier = Modifier.weight(1f).height(64.dp)
            ) {
                Column(horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally) {
                    Icon(Icons.Filled.VolumeMute, contentDescription = "Mute")
                    Text("Mute", style = MaterialTheme.typography.labelSmall)
                }
            }
            HoldRepeatButton(
                icon = Icons.Filled.VolumeUp,
                label = "Volume up",
                modifier = Modifier.weight(1f).height(64.dp)
            ) { connection.sendMedia("vol_up") }
        }
    }
}

@Composable
private fun MediaButton(
    icon: ImageVector,
    label: String,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    OutlinedIconButton(onClick = onClick, modifier = modifier) {
        Column(horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally) {
            Icon(icon, contentDescription = label)
            Text(label, style = MaterialTheme.typography.labelSmall)
        }
    }
}

/**
 * Fires [onRepeat] once per tap, then every 150ms while held (FR4.3).
 * The built-in onClick stays as the accessible single-step action (TalkBack);
 * the press handler only takes over for holds longer than 250ms and marks the
 * tap as already handled so a hold never double-fires.
 */
@Composable
private fun HoldRepeatButton(
    icon: ImageVector,
    label: String,
    modifier: Modifier = Modifier,
    onRepeat: () -> Unit
) {
    var handledByHold by remember { mutableStateOf(false) }
    val interaction = remember { androidx.compose.foundation.interaction.MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    // Hold-to-repeat: after a 300ms press threshold, repeat every 150ms.
    // Quick taps fall through to onClick; a hold suppresses the tap's extra
    // step. Runs outside the pointer scope — delay is allowed here.
    LaunchedEffect(interaction) {
        snapshotFlow { pressed }.collect { isPressedNow ->
            if (isPressedNow) {
                delay(300)
                if (pressed) {
                    handledByHold = true
                    while (pressed) {
                        onRepeat()
                        delay(150)
                    }
                    handledByHold = false
                }
            }
        }
    }
    OutlinedIconButton(
        onClick = {
            if (!handledByHold) onRepeat()
            handledByHold = false
        },
        interactionSource = interaction,
        modifier = modifier
            .semantics { contentDescription = "$label (hold to repeat)" }
    ) {
        Column(horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally) {
            Icon(icon, contentDescription = null)
            Text(label, style = MaterialTheme.typography.labelSmall)
        }
    }
}