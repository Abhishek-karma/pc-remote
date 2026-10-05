package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.example.pcremote.connection.Keys
import com.example.pcremote.remote.TextDiff

/**
 * The key row under the touchpad: modifiers, navigation, and text entry.
 *
 * There is no on-screen keyboard here. The phone already has a good one, so text
 * is typed into a real text field with the normal IME and sent to the PC as it is
 * typed - which is faster, and gets autocorrect and accents for free.
 */
@Composable
fun KeyBar(
    latched: Set<String>,
    onModifier: (String) -> Unit,
    onKey: (String) -> Unit,
    onText: (String) -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth()) {
        HorizontalDivider()

        TextEntry(onText)

        KeyRow(Keys.MODIFIERS.map { it to label(it) }, latched, onModifier)

        KeyRow(
            listOf(
                "ESC" to "Esc",
                "TAB" to "Tab",
                "BACKSPACE" to "Del",
                "ENTER" to "Enter",
            ),
            emptySet(),
        ) { onKey(it) }

        KeyRow(
            listOf(
                "LEFT" to "←",
                "DOWN" to "↓",
                "UP" to "↑",
                "RIGHT" to "→",
                "HOME" to "Home",
                "END" to "End",
            ),
            emptySet(),
        ) { onKey(it) }
    }
}

/**
 * A one-line text field using the phone's own keyboard.
 *
 * Only what was just added is sent, never the whole field: sending the full value
 * on every keystroke would retype the entire sentence each time a character was
 * appended. A deletion is sent as a Backspace, which is what the user expects.
 */
@Composable
private fun TextEntry(onText: (String) -> Unit) {
    var text by remember { mutableStateOf("") }

    BasicTextField(
        value = text,
        onValueChange = { updated ->
            TextDiff.between(previous = text, updated = updated)?.let(onText)
            text = updated
        },

        textStyle = LocalTextStyle.current.copy(color = MaterialTheme.colorScheme.onSurface),
        cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
        singleLine = true,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
        decorationBox = { inner ->
            Box {
                if (text.isEmpty()) {
                    Text(
                        text = "Type here…",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                inner()
            }
        },
    )
}

@Composable
private fun KeyRow(
    keys: List<Pair<String, String>>,
    latched: Set<String>,
    onPress: (String) -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 3.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        keys.forEach { (name, caption) ->
            KeyButton(
                caption = caption,
                held = name in latched,
                onClick = { onPress(name) },
                modifier = Modifier.weight(1f),
            )
        }
    }
}

@Composable
private fun KeyButton(
    caption: String,
    held: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(
        color = if (held) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.surfaceVariant,
        shape = MaterialTheme.shapes.small,
        modifier = modifier.height(44.dp),
        onClick = onClick,
    ) {
        Box(contentAlignment = Alignment.Center) {
            Text(
                text = caption,
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.Medium,
                color = if (held) MaterialTheme.colorScheme.onPrimary
                        else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

private fun label(key: String): String = when (key) {
    Keys.CTRL -> "Ctrl"
    Keys.ALT -> "Alt"
    Keys.SHIFT -> "Shift"
    Keys.WIN -> "Win"
    else -> key
}