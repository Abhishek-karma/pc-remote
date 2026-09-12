package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import kotlinx.coroutines.delay

private val MODIFIERS = listOf("CTRL", "ALT", "SHIFT", "WIN")

// Display label -> the key name the protocol expects (07-API-SPECIFICATION.md §4.7).
private val SPECIAL_KEYS = listOf(
    "Esc" to "ESC",
    "Tab" to "TAB",
    "Up" to "UP",
    "Del" to "DELETE",
    "Left" to "LEFT",
    "Down" to "DOWN",
    "Right" to "RIGHT",
    "Bksp" to "BACKSPACE"
)

/**
 * Keyboard screen (02-FEATURE-SPECIFICATION.md F3): a modifier-lock row, a
 * special-key grid, and a debounced text field. All controls are plain text
 * buttons with the label as their accessibility text (13-ACCESSIBILITY.md §2).
 *
 * Modifier-lock: tapping a modifier highlights it; the *next* key press is
 * sent with those modifiers held and then the lock clears (FR3.4).
 */
@Composable
fun KeyboardScreen(connection: RemoteConnection) {
    // Currently locked modifiers (e.g. "CTRL" while composing Ctrl+C).
    var lockedModifiers by remember { mutableStateOf(setOf<String>()) }
    var text by remember { mutableStateOf("") }

    // Debounce typed text into text_input messages (03-USER-FLOWS.md UF4).
    LaunchedEffect(text) {
        if (text.isNotEmpty()) {
            delay(300)
            connection.sendText(text)
        }
    }

    fun sendKeyWithModifiers(key: String) {
        connection.sendKey(key, lockedModifiers.toList())
        lockedModifiers = emptySet()
    }

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("Keyboard", style = MaterialTheme.typography.titleMedium)

        // Modifier-lock row.
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            MODIFIERS.forEach { modifierName ->
                val locked = modifierName in lockedModifiers
                FilledTonalButton(
                    onClick = {
                        lockedModifiers = if (locked) lockedModifiers - modifierName
                        else lockedModifiers + modifierName
                    },
                    modifier = Modifier.weight(1f).heightIn(min = 48.dp)
                        .semantics { this.selected = locked }
                ) {
                    Text(
                        text = modifierName,
                        color = if (locked) MaterialTheme.colorScheme.onPrimaryContainer
                        else MaterialTheme.colorScheme.onSurface
                    )
                }
            }
        }

        // Special-key grid — 2 rows of 4, mirroring 04-UI-UX-SPECIFICATION.md §5.
        SPECIAL_KEYS.chunked(4).forEach { rowKeys ->
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                rowKeys.forEach { (label, keyName) ->
                    OutlinedButton(
                        onClick = { sendKeyWithModifiers(keyName) },
                        modifier = Modifier.weight(1f).heightIn(min = 56.dp)
                    ) {
                        Text(label)
                    }
                }
            }
        }

        // Text entry + Enter.
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                label = { Text("Type here") },
                modifier = Modifier.weight(1f)
            )
            OutlinedButton(
                onClick = { sendKeyWithModifiers("ENTER") },
                modifier = Modifier.heightIn(min = 56.dp).padding(top = 4.dp)
            ) {
                Text("Enter")
            }
        }
    }
}