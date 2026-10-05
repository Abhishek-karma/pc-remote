package com.example.pcremote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.pcremote.settings.Settings

/** Settings: pointer speed, haptics, and forgetting a paired PC. */
@Composable
fun SettingsScreen(
    sensitivity: Float,
    onSensitivity: (Float) -> Unit,
    haptics: Boolean,
    onHaptics: (Boolean) -> Unit,
    pairedNames: List<String>,
    onForget: (String) -> Unit,
    onBack: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text("Settings", style = MaterialTheme.typography.titleMedium)

        Column {
            Text("Pointer speed", style = MaterialTheme.typography.bodyLarge)
            Slider(
                value = sensitivity,
                onValueChange = onSensitivity,
                valueRange = Settings.MIN_SENSITIVITY..Settings.MAX_SENSITIVITY,
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text("Vibrate on tap", style = MaterialTheme.typography.bodyLarge)
            Switch(checked = haptics, onCheckedChange = onHaptics)
        }

        HorizontalDivider()

        if (pairedNames.isEmpty()) {
            Text(
                text = "No PCs paired yet.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        } else {
            Text("Paired PCs", style = MaterialTheme.typography.bodyLarge)
            pairedNames.forEach { name ->
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    Text(name, style = MaterialTheme.typography.bodyMedium)
                    Button(onClick = { onForget(name) }) { Text("Forget") }
                }
            }
        }

        Button(onClick = onBack) { Text("Back") }
    }
}