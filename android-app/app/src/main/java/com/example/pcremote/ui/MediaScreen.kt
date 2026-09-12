package com.example.pcremote.ui

import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

/**
 * Media screen (02-FEATURE-SPECIFICATION.md F4): transport buttons and
 * press-and-hold volume steps — holding a volume button repeats the action
 * every 150ms (FR4.3). Action names match 07-API-SPECIFICATION.md §4.9.
 */
@Composable
fun MediaScreen(connection: RemoteConnection) {
    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Text("Media", style = MaterialTheme.typography.titleMedium)

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Button(
                onClick = { connection.sendMedia("prev") },
                modifier = Modifier.weight(1f).heightIn(min = 72.dp)
            ) { Text("Prev") }
            Button(
                onClick = { connection.sendMedia("play_pause") },
                modifier = Modifier.weight(1.4f).heightIn(min = 72.dp)
            ) { Text("Play / Pause") }
            Button(
                onClick = { connection.sendMedia("next") },
                modifier = Modifier.weight(1f).heightIn(min = 72.dp)
            ) { Text("Next") }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            OutlinedButton(
                onClick = { connection.sendMedia("vol_down") },
                modifier = Modifier.weight(1f).heightIn(min = 64.dp)
                    .repeatWhilePressed { connection.sendMedia("vol_down") }
            ) { Text("Volume −") }
            OutlinedButton(
                onClick = { connection.sendMedia("mute") },
                modifier = Modifier.weight(1f).heightIn(min = 64.dp)
            ) { Text("Mute") }
            OutlinedButton(
                onClick = { connection.sendMedia("vol_up") },
                modifier = Modifier.weight(1f).heightIn(min = 64.dp)
                    .repeatWhilePressed { connection.sendMedia("vol_up") }
            ) { Text("Volume +") }
        }
    }
}

/**
 * Fires [onRepeat] once on press, then every 150ms while the button is held
 * (FR4.3). A plain tap still fires exactly once.
 */
private fun Modifier.repeatWhilePressed(onRepeat: () -> Unit): Modifier = pointerInput(onRepeat) {
    detectTapGestures(
        onPress = {
            onRepeat()
            try {
                while (true) {
                    delay(150)
                    onRepeat()
                }
            } catch (cancelled: CancellationException) {
                // Finger lifted or the gesture was cancelled — end the repeat.
            }
        }
    )
}