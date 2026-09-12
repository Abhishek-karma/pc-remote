package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.theme.TouchTarget
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
 * Remote keyboard (02 F3): modifier-lock row, special-key grid, debounced text.
 *
 * Text input sends only the *delta* since the last send: appended characters
 * go as `text_input`, deletions as BACKSPACE presses. Resending the whole
 * buffer (the old approach) duplicated text on the PC after every pause.
 */
@Composable
fun KeyboardScreen(connection: RemoteConnection) {
    var lockedModifiers by remember { mutableStateOf(setOf<String>()) }
    var text by remember { mutableStateOf("") }
    var sentLength by remember { mutableIntStateOf(0) }

    // Debounced delta send (03-USER-FLOWS.md UF4).
    LaunchedEffect(text) {
        if (text.length == sentLength) return@LaunchedEffect
        delay(300)
        when {
            text.length > sentLength && text.startsWith(text.take(sentLength)) -> {
                connection.sendText(text.substring(sentLength))
                sentLength = text.length
            }
            text.length < sentLength && sentLength > 0 -> {
                // Deletions since the last send — echo them as backspaces.
                repeat((sentLength - text.length).coerceAtMost(32)) {
                    connection.sendKey("BACKSPACE")
                }
                sentLength = text.length
            }
            else -> {
                // Edited in the middle: can't express as a delta safely —
                // clear back to the field state with backspaces, then retype.
                repeat(sentLength.coerceAtMost(64)) { connection.sendKey("BACKSPACE") }
                if (text.isNotEmpty()) connection.sendText(text)
                sentLength = text.length
            }
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
        // Modifier-lock row: selected state is filled + checkmarked, never
        // color alone (13 §2).
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
                    modifier = Modifier
                        .weight(1f)
                        .height(TouchTarget.minimum)
                        .semantics {
                            this.selected = locked
                            contentDescription =
                                "$modifierName modifier, ${if (locked) "on" else "off"}"
                        },
                    colors = androidx.compose.material3.ButtonDefaults.filledTonalButtonColors(
                        containerColor = if (locked) MaterialTheme.colorScheme.primary
                        else MaterialTheme.colorScheme.secondaryContainer,
                        contentColor = if (locked) MaterialTheme.colorScheme.onPrimary
                        else MaterialTheme.colorScheme.onSecondaryContainer
                    )
                ) {
                    Text(
                        (if (locked) "\u2713 " else "") + modifierName,
                        fontWeight = if (locked) androidx.compose.ui.text.font.FontWeight.Bold
                        else androidx.compose.ui.text.font.FontWeight.Normal
                    )
                }
            }
        }

        // Special-key grid (04 §5).
        SPECIAL_KEYS.chunked(4).forEach { rowKeys ->
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                rowKeys.forEach { (label, keyName) ->
                    OutlinedButton(
                        onClick = { sendKeyWithModifiers(keyName) },
                        modifier = Modifier
                            .weight(1f)
                            .height(52.dp)
                    ) {
                        Text(label)
                    }
                }
            }
        }

        // Text entry + Enter.
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                label = { Text("Type here") },
                singleLine = false,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text),
                modifier = Modifier.weight(1f)
            )
            OutlinedButton(
                onClick = { sendKeyWithModifiers("ENTER") },
                modifier = Modifier
                    .height(56.dp)
                    .width(64.dp)
                    .semantics { contentDescription = "Enter key" }
            ) {
                Text("\u21B5", style = MaterialTheme.typography.titleLarge)
            }
        }
    }
}