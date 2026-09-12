package com.example.pcremote.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import kotlin.math.roundToInt

/**
 * Touchpad screen: a large gesture surface for cursor movement/clicks, or a
 * D-pad (button-based movement) as the TalkBack-accessible alternative
 * (13-ACCESSIBILITY.md §2). Both share the sensitivity setting from Settings.
 */
@Composable
fun TouchpadScreen(connection: RemoteConnection, sensitivity: Float = 1.5f) {
    var dpadMode by rememberSaveable { mutableStateOf(false) }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "Touchpad",
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.padding(bottom = 0.dp)
            )
            TextButton(onClick = { dpadMode = !dpadMode }) {
                Text(if (dpadMode) "Switch to gestures" else "D-pad mode")
            }
        }

        if (dpadMode) {
            DPadSurface(
                modifier = Modifier.fillMaxWidth().weight(1f),
                connection = connection,
                sensitivity = sensitivity
            )
        } else {
            TouchSurface(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                connection = connection,
                sensitivity = sensitivity
            )
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(64.dp)
                .padding(top = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                modifier = Modifier.weight(1f).fillMaxSize(),
                onClick = { connection.sendMouseClick(button = "left", action = "click") }
            ) { Text("Left") }

            Button(
                modifier = Modifier.weight(1f).fillMaxSize(),
                onClick = { connection.sendMouseClick(button = "right", action = "click") }
            ) { Text("Right") }
        }
    }
}

@Composable
private fun TouchSurface(modifier: Modifier, connection: RemoteConnection, sensitivity: Float) {
    // Track accumulated drag so we can throttle-free send small relative deltas
    // as they happen; OkHttp/WebSocket handles backpressure fine at this rate
    // for a touchpad, but you can add a simple time-based throttle here if
    // you see jank on very fast swipes.
    Box(
        modifier = modifier
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .pointerInput(Unit) {
                detectDragGestures { change, dragAmount ->
                    change.consume()
                    val dx = (dragAmount.x * sensitivity).roundToInt()
                    val dy = (dragAmount.y * sensitivity).roundToInt()
                    if (dx != 0 || dy != 0) connection.sendMouseMove(dx, dy)
                }
            }
            .pointerInput(Unit) {
                detectTapGestures(
                    onTap = { connection.sendMouseClick(button = "left", action = "click") },
                    onLongPress = { connection.sendMouseClick(button = "right", action = "click") }
                )
            },
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = "Drag to move • Tap = left click • Long-press = right click",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(16.dp)
        )
    }
}

/**
 * Discrete cursor movement for touch-free / TalkBack users: each arrow sends
 * a fixed relative move (scaled by the sensitivity setting), and the center
 * button left-clicks. Every control is a plain text button with its label as
 * the accessibility text (13-ACCESSIBILITY.md §2).
 */
@Composable
private fun DPadSurface(modifier: Modifier, connection: RemoteConnection, sensitivity: Float) {
    val step = (60 * sensitivity).roundToInt().coerceAtLeast(8)

    Column(
        modifier = modifier.background(MaterialTheme.colorScheme.surfaceVariant).padding(16.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        DPadButton("Up", modifier = Modifier.fillMaxWidth(0.5f).padding(4.dp)) {
            connection.sendMouseMove(0, -step)
        }
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
            DPadButton("Left", modifier = Modifier.weight(1f).padding(4.dp)) {
                connection.sendMouseMove(-step, 0)
            }
            DPadButton("Click", modifier = Modifier.weight(1f).padding(4.dp)) {
                connection.sendMouseClick()
            }
            DPadButton("Right", modifier = Modifier.weight(1f).padding(4.dp)) {
                connection.sendMouseMove(step, 0)
            }
        }
        DPadButton("Down", modifier = Modifier.fillMaxWidth(0.5f).padding(4.dp)) {
            connection.sendMouseMove(0, step)
        }
    }
}

@Composable
private fun DPadButton(label: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    OutlinedButton(onClick = onClick, modifier = modifier.height(56.dp)) { Text(label) }
}