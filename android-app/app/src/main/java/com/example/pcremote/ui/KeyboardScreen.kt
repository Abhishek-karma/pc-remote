package com.example.pcremote.ui

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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Backspace
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowLeft
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.KeyboardReturn
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Surface
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.theme.RemoteColors
import kotlinx.coroutines.delay

private val MODIFIER_KEYS = listOf("CTRL", "ALT", "SHIFT", "WIN")

enum class FunctionKeyLayout {
    GROUPED_4X3,
    COMPACT_6X2
}

private data class FunctionKeyDef(
    val key: String,
    val subtitle: String,
    val description: String
)

private val ALL_FUNCTION_KEYS = listOf(
    // Group 1: F1 - F4
    FunctionKeyDef("F1", "Help", "F1 Help"),
    FunctionKeyDef("F2", "Rename", "F2 Rename file"),
    FunctionKeyDef("F3", "Search", "F3 Search"),
    FunctionKeyDef("F4", "Close", "F4 Address or Close"),
    // Group 2: F5 - F8
    FunctionKeyDef("F5", "Refresh", "F5 Refresh page"),
    FunctionKeyDef("F6", "Focus", "F6 Address Bar Focus"),
    FunctionKeyDef("F7", "Caret", "F7 Caret Browsing"),
    FunctionKeyDef("F8", "Select", "F8 Extend Selection or Safe Mode"),
    // Group 3: F9 - F12
    FunctionKeyDef("F9", "Recalc", "F9 Recalculate or Send Mail"),
    FunctionKeyDef("F10", "Menu", "F10 Menu Bar"),
    FunctionKeyDef("F11", "Full", "F11 Toggle Fullscreen"),
    FunctionKeyDef("F12", "Dev", "F12 Developer Tools or Save As")
)

/**
 * Production-ready Keyboard Controller:
 * - Dedicated Function Keys (F1–F12) with Grouped 4x3 & Compact 6x2 views
 * - Quick function combinations (Alt+F4, Ctrl+F5, Shift+F10, F11)
 * - Solves text wrapping: perfectly dimensioned technical key grid
 * - Latching modifier keys with glowing status indicators
 * - Quick shortcuts row (Ctrl+C, Ctrl+V, Ctrl+Z, Alt+Tab, Win)
 * - Distinct inverted-T cursor navigation cluster
 * - High-speed delta-based text composer with instant clear and send
 */
@Composable
fun KeyboardScreen(
    connection: RemoteConnection,
    hapticsEnabled: Boolean = true
) {
    var lockedModifiers by remember { mutableStateOf(setOf<String>()) }
    var text by remember { mutableStateOf("") }
    var sentLength by remember { mutableIntStateOf(0) }
    var functionKeyLayout by remember { mutableStateOf(FunctionKeyLayout.GROUPED_4X3) }
    val haptics = LocalHapticFeedback.current

    fun performHaptic() {
        if (hapticsEnabled) haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
    }

    // Debounced delta-based text streamer
    LaunchedEffect(text) {
        if (text.length == sentLength) return@LaunchedEffect
        delay(250)
        when {
            text.length > sentLength && text.startsWith(text.take(sentLength)) -> {
                connection.sendText(text.substring(sentLength))
                sentLength = text.length
            }
            text.length < sentLength && sentLength > 0 -> {
                repeat((sentLength - text.length).coerceAtMost(32)) {
                    connection.sendKey("BACKSPACE")
                }
                sentLength = text.length
            }
            else -> {
                repeat(sentLength.coerceAtMost(64)) { connection.sendKey("BACKSPACE") }
                if (text.isNotEmpty()) connection.sendText(text)
                sentLength = text.length
            }
        }
    }

    fun sendKeyAction(key: String, additionalModifiers: List<String> = emptyList()) {
        performHaptic()
        val combinedModifiers = (lockedModifiers + additionalModifiers).distinct()
        connection.sendKey(key, combinedModifiers)
        // Auto-release locked modifiers after firing
        lockedModifiers = emptySet()
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // --- 1. Text Composer Input Deck ---
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "LIVE INPUT STREAM",
                        style = MaterialTheme.typography.labelSmall.copy(
                            letterSpacing = 1.5.sp,
                            fontWeight = FontWeight.SemiBold
                        ),
                        color = MaterialTheme.colorScheme.primary
                    )
                    if (text.isNotEmpty()) {
                        Text(
                            text = "${text.length} chars",
                            style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    OutlinedTextField(
                        value = text,
                        onValueChange = { text = it },
                        placeholder = { Text("Type here to send directly to PC…", fontSize = 14.sp) },
                        singleLine = true,
                        trailingIcon = {
                            if (text.isNotEmpty()) {
                                IconButton(onClick = {
                                    text = ""
                                    sentLength = 0
                                }) {
                                    Icon(
                                        Icons.Filled.Clear,
                                        contentDescription = "Clear text",
                                        modifier = Modifier.size(18.dp)
                                    )
                                }
                            }
                        },
                        keyboardOptions = KeyboardOptions(
                            keyboardType = KeyboardType.Text,
                            imeAction = ImeAction.Send
                        ),
                        keyboardActions = KeyboardActions(
                            onSend = {
                                sendKeyAction("ENTER")
                                text = ""
                                sentLength = 0
                            }
                        ),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = MaterialTheme.colorScheme.primary,
                            unfocusedBorderColor = MaterialTheme.colorScheme.outline.copy(alpha = 0.4f),
                            focusedContainerColor = MaterialTheme.colorScheme.surface,
                            unfocusedContainerColor = MaterialTheme.colorScheme.surface
                        ),
                        modifier = Modifier.weight(1f)
                    )

                    // Send / Enter Button
                    Surface(
                        onClick = {
                            sendKeyAction("ENTER")
                            text = ""
                            sentLength = 0
                        },
                        shape = RoundedCornerShape(12.dp),
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier
                            .height(54.dp)
                            .width(62.dp)
                    ) {
                        Box(contentAlignment = Alignment.Center) {
                            Icon(
                                Icons.Outlined.KeyboardReturn,
                                contentDescription = "Send Enter key",
                                tint = MaterialTheme.colorScheme.onPrimary,
                                modifier = Modifier.size(22.dp)
                            )
                        }
                    }
                }
            }
        }

        // --- 2. Latching Modifier Keys Row ---
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(
                text = "MODIFIERS (LATCHING)",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                MODIFIER_KEYS.forEach { modName ->
                    val isLocked = modName in lockedModifiers
                    TactileModifierKey(
                        name = modName,
                        isLocked = isLocked,
                        modifier = Modifier.weight(1f),
                        onClick = {
                            performHaptic()
                            lockedModifiers = if (isLocked) lockedModifiers - modName else lockedModifiers + modName
                        }
                    )
                }
            }
        }

        // --- 3. Quick PC Shortcuts Deck ---
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(
                text = "QUICK SHORTCUTS",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                QuickShortcutButton("Ctrl+C", "Copy", Modifier.weight(1f)) {
                    sendKeyAction("C", listOf("CTRL"))
                }
                QuickShortcutButton("Ctrl+V", "Paste", Modifier.weight(1f)) {
                    sendKeyAction("V", listOf("CTRL"))
                }
                QuickShortcutButton("Ctrl+Z", "Undo", Modifier.weight(1f)) {
                    sendKeyAction("Z", listOf("CTRL"))
                }
                QuickShortcutButton("Ctrl+A", "All", Modifier.weight(1f)) {
                    sendKeyAction("A", listOf("CTRL"))
                }
                QuickShortcutButton("Alt+Tab", "Switch", Modifier.weight(1.1f)) {
                    sendKeyAction("TAB", listOf("ALT"))
                }
                QuickShortcutButton("Alt+F4", "Close", Modifier.weight(1.1f)) {
                    sendKeyAction("F4", listOf("ALT"))
                }
                QuickShortcutButton("Win", "Menu", Modifier.weight(1f)) {
                    sendKeyAction("WIN")
                }
            }
        }

        // --- 4. Function Keys Deck (F1 – F12) ---
        Surface(
            shape = RoundedCornerShape(14.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
            modifier = Modifier
                .fillMaxWidth()
                .testTag("function_keys_deck")
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                // Header with title and 4x3 / 6x2 layout switcher
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        Text(
                            text = "FUNCTION KEYS",
                            style = MaterialTheme.typography.labelSmall.copy(
                                letterSpacing = 1.4.sp,
                                fontWeight = FontWeight.SemiBold
                            ),
                            color = MaterialTheme.colorScheme.primary
                        )
                        Text(
                            text = "F1–F12",
                            style = MaterialTheme.typography.labelSmall.copy(
                                fontFamily = FontFamily.Monospace,
                                fontSize = 11.sp,
                                fontWeight = FontWeight.Medium
                            ),
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }

                    // Layout Mode Segmented Control
                    Surface(
                        shape = RoundedCornerShape(8.dp),
                        color = MaterialTheme.colorScheme.surfaceContainerHigh,
                        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f))
                    ) {
                        Row(modifier = Modifier.padding(2.dp)) {
                            val isGrouped = functionKeyLayout == FunctionKeyLayout.GROUPED_4X3
                            Box(
                                modifier = Modifier
                                    .clip(RoundedCornerShape(6.dp))
                                    .background(if (isGrouped) MaterialTheme.colorScheme.primary else Color.Transparent)
                                    .clickable {
                                        performHaptic()
                                        functionKeyLayout = FunctionKeyLayout.GROUPED_4X3
                                    }
                                    .padding(horizontal = 8.dp, vertical = 4.dp)
                                    .testTag("layout_4x3_button")
                            ) {
                                Text(
                                    text = "4\u00D73",
                                    style = MaterialTheme.typography.labelSmall.copy(
                                        fontWeight = FontWeight.Bold,
                                        fontSize = 10.sp
                                    ),
                                    color = if (isGrouped) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            Box(
                                modifier = Modifier
                                    .clip(RoundedCornerShape(6.dp))
                                    .background(if (!isGrouped) MaterialTheme.colorScheme.primary else Color.Transparent)
                                    .clickable {
                                        performHaptic()
                                        functionKeyLayout = FunctionKeyLayout.COMPACT_6X2
                                    }
                                    .padding(horizontal = 8.dp, vertical = 4.dp)
                                    .testTag("layout_6x2_button")
                            ) {
                                Text(
                                    text = "6\u00D72",
                                    style = MaterialTheme.typography.labelSmall.copy(
                                        fontWeight = FontWeight.Bold,
                                        fontSize = 10.sp
                                    ),
                                    color = if (!isGrouped) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                    }
                }

                // Keys Matrix
                if (functionKeyLayout == FunctionKeyLayout.GROUPED_4X3) {
                    // 3 rows of 4 with action subtitles
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        // Row 1: F1 - F4
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            ALL_FUNCTION_KEYS.subList(0, 4).forEach { fKey ->
                                FunctionKeyButton(
                                    key = fKey.key,
                                    subtitle = fKey.subtitle,
                                    description = fKey.description,
                                    modifier = Modifier.weight(1f),
                                    onClick = { sendKeyAction(fKey.key) }
                                )
                            }
                        }
                        // Row 2: F5 - F8
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            ALL_FUNCTION_KEYS.subList(4, 8).forEach { fKey ->
                                FunctionKeyButton(
                                    key = fKey.key,
                                    subtitle = fKey.subtitle,
                                    description = fKey.description,
                                    modifier = Modifier.weight(1f),
                                    isHighlighted = fKey.key == "F5",
                                    onClick = { sendKeyAction(fKey.key) }
                                )
                            }
                        }
                        // Row 3: F9 - F12
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            ALL_FUNCTION_KEYS.subList(8, 12).forEach { fKey ->
                                FunctionKeyButton(
                                    key = fKey.key,
                                    subtitle = fKey.subtitle,
                                    description = fKey.description,
                                    modifier = Modifier.weight(1f),
                                    isHighlighted = fKey.key == "F11",
                                    onClick = { sendKeyAction(fKey.key) }
                                )
                            }
                        }
                    }
                } else {
                    // 2 rows of 6 compact
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(6.dp)
                        ) {
                            ALL_FUNCTION_KEYS.subList(0, 6).forEach { fKey ->
                                FunctionKeyButton(
                                    key = fKey.key,
                                    subtitle = null,
                                    description = fKey.description,
                                    modifier = Modifier.weight(1f),
                                    isHighlighted = fKey.key == "F5",
                                    onClick = { sendKeyAction(fKey.key) }
                                )
                            }
                        }
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(6.dp)
                        ) {
                            ALL_FUNCTION_KEYS.subList(6, 12).forEach { fKey ->
                                FunctionKeyButton(
                                    key = fKey.key,
                                    subtitle = null,
                                    description = fKey.description,
                                    modifier = Modifier.weight(1f),
                                    isHighlighted = fKey.key == "F11",
                                    onClick = { sendKeyAction(fKey.key) }
                                )
                            }
                        }
                    }
                }

                // Quick Function Combos Strip
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(6.dp)
                ) {
                    QuickComboChip(
                        label = "Alt+F4",
                        hint = "Close App",
                        modifier = Modifier.weight(1f),
                        onClick = { sendKeyAction("F4", listOf("ALT")) }
                    )
                    QuickComboChip(
                        label = "Ctrl+F5",
                        hint = "Hard Reload",
                        modifier = Modifier.weight(1f),
                        onClick = { sendKeyAction("F5", listOf("CTRL")) }
                    )
                    QuickComboChip(
                        label = "Shift+F10",
                        hint = "Right Click",
                        modifier = Modifier.weight(1f),
                        onClick = { sendKeyAction("F10", listOf("SHIFT")) }
                    )
                    QuickComboChip(
                        label = "F11",
                        hint = "Fullscreen",
                        modifier = Modifier.weight(0.9f),
                        onClick = { sendKeyAction("F11") }
                    )
                }
            }
        }

        // --- 5. Essential Function & Editing Keys ---
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(
                text = "SYSTEM & EDITING KEYS",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                TactileFunctionKey("ESC", Modifier.weight(1f)) { sendKeyAction("ESC") }
                TactileFunctionKey("TAB", Modifier.weight(1f)) { sendKeyAction("TAB") }
                TactileFunctionKey("BKSP", Modifier.weight(1f), icon = Icons.Filled.Backspace) { sendKeyAction("BACKSPACE") }
                TactileFunctionKey("DEL", Modifier.weight(1f)) { sendKeyAction("DELETE") }
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                TactileFunctionKey("INS", Modifier.weight(1f)) { sendKeyAction("INSERT") }
                TactileFunctionKey("PRTSC", Modifier.weight(1f)) { sendKeyAction("PRINTSCREEN") }
                TactileFunctionKey("PGUP", Modifier.weight(1f)) { sendKeyAction("PAGEUP") }
                TactileFunctionKey("PGDN", Modifier.weight(1f)) { sendKeyAction("PAGEDOWN") }
            }
        }

        // --- 6. Dedicated Inverted-T Cursor Cluster & Space ---
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(14.dp))
                .background(MaterialTheme.colorScheme.surfaceContainer)
                .border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f), RoundedCornerShape(14.dp))
                .padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "NAVIGATION ARROWS",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            // Arrow Up
            TactileArrowKey(
                icon = Icons.Filled.KeyboardArrowUp,
                description = "Arrow Up",
                onClick = { sendKeyAction("UP") }
            )

            // Arrows Left, Down, Right
            Row(
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                TactileArrowKey(
                    icon = Icons.Filled.KeyboardArrowLeft,
                    description = "Arrow Left",
                    onClick = { sendKeyAction("LEFT") }
                )
                TactileArrowKey(
                    icon = Icons.Filled.KeyboardArrowDown,
                    description = "Arrow Down",
                    onClick = { sendKeyAction("DOWN") }
                )
                TactileArrowKey(
                    icon = Icons.Filled.KeyboardArrowRight,
                    description = "Arrow Right",
                    onClick = { sendKeyAction("RIGHT") }
                )
            }

            Spacer(modifier = Modifier.height(4.dp))

            // Wide Spacebar + Home/End Row
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                TactileFunctionKey("HOME", Modifier.weight(0.7f)) { sendKeyAction("HOME") }

                // Long Space Key
                Surface(
                    onClick = { sendKeyAction("SPACE") },
                    shape = RoundedCornerShape(10.dp),
                    color = MaterialTheme.colorScheme.surfaceVariant,
                    border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)),
                    modifier = Modifier
                        .weight(2f)
                        .height(48.dp)
                ) {
                    Box(contentAlignment = Alignment.Center) {
                        Text(
                            text = "SPACE",
                            style = MaterialTheme.typography.labelMedium.copy(
                                fontWeight = FontWeight.SemiBold,
                                letterSpacing = 1.5.sp
                            ),
                            color = MaterialTheme.colorScheme.onSurface
                        )
                    }
                }

                TactileFunctionKey("END", Modifier.weight(0.7f)) { sendKeyAction("END") }
            }
        }
    }
}

/**
 * Modifier button with illumination indicator dot for latching state.
 */
@Composable
private fun TactileModifierKey(
    name: String,
    isLocked: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    val bg = if (isLocked) RemoteColors.KeyActiveBackground else MaterialTheme.colorScheme.surfaceContainerHigh
    val border = if (isLocked) RemoteColors.KeyActiveBorder else MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)
    val fg = if (isLocked) Color.White else MaterialTheme.colorScheme.onSurface

    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(10.dp),
        color = bg,
        border = androidx.compose.foundation.BorderStroke(1.dp, border),
        modifier = modifier
            .height(50.dp)
            .semantics {
                this.selected = isLocked
                contentDescription = "$name key ${if (isLocked) "locked" else "unlocked"}"
            }
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                if (isLocked) {
                    Box(
                        modifier = Modifier
                            .size(6.dp)
                            .background(RemoteColors.Accent, CircleShape)
                    )
                }
                Text(
                    text = name,
                    style = MaterialTheme.typography.labelMedium.copy(
                        fontWeight = FontWeight.Bold,
                        fontFamily = FontFamily.Monospace,
                        fontSize = 13.sp
                    ),
                    color = fg
                )
            }
            Text(
                text = if (isLocked) "ON" else "OFF",
                style = MaterialTheme.typography.labelSmall.copy(
                    fontSize = 9.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = if (isLocked) RemoteColors.Accent else MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun QuickShortcutButton(
    title: String,
    subtitle: String,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
        modifier = modifier.height(48.dp)
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Text(
                text = title,
                style = MaterialTheme.typography.labelSmall.copy(
                    fontWeight = FontWeight.Bold,
                    fontSize = 11.sp
                ),
                color = MaterialTheme.colorScheme.onSurface
            )
            Text(
                text = subtitle,
                style = MaterialTheme.typography.bodySmall.copy(fontSize = 9.sp),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun FunctionKeyButton(
    key: String,
    subtitle: String? = null,
    description: String? = null,
    modifier: Modifier = Modifier,
    isHighlighted: Boolean = false,
    onClick: () -> Unit
) {
    val containerColor = if (isHighlighted) {
        MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.5f)
    } else {
        MaterialTheme.colorScheme.surfaceContainerHigh
    }
    val borderColor = if (isHighlighted) {
        MaterialTheme.colorScheme.primary.copy(alpha = 0.6f)
    } else {
        MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)
    }
    val textColor = if (isHighlighted) {
        MaterialTheme.colorScheme.primary
    } else {
        MaterialTheme.colorScheme.onSurface
    }

    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(10.dp),
        color = containerColor,
        border = androidx.compose.foundation.BorderStroke(1.dp, borderColor),
        modifier = modifier
            .height(48.dp)
            .testTag("key_${key.lowercase()}")
            .semantics {
                this.contentDescription = description ?: "$key key"
            }
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Text(
                text = key,
                style = MaterialTheme.typography.labelMedium.copy(
                    fontWeight = FontWeight.Bold,
                    fontFamily = FontFamily.Monospace,
                    fontSize = 13.sp
                ),
                color = textColor
            )
            if (subtitle != null) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall.copy(
                        fontSize = 9.sp,
                        fontWeight = FontWeight.Medium
                    ),
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

@Composable
private fun QuickComboChip(
    label: String,
    hint: String,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
        modifier = modifier
            .height(38.dp)
            .testTag("combo_${label.lowercase().replace("+", "_")}")
            .semantics { contentDescription = "$label combo $hint" }
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall.copy(
                    fontWeight = FontWeight.Bold,
                    fontFamily = FontFamily.Monospace,
                    fontSize = 10.5.sp
                ),
                color = MaterialTheme.colorScheme.primary
            )
            Text(
                text = hint,
                style = MaterialTheme.typography.bodySmall.copy(
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Normal
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun TactileFunctionKey(
    label: String,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(10.dp),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)),
        modifier = modifier
            .height(48.dp)
            .testTag("key_${label.lowercase()}")
    ) {
        Row(
            modifier = Modifier.fillMaxSize(),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            if (icon != null) {
                Icon(
                    imageVector = icon,
                    contentDescription = label,
                    tint = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.size(16.dp).padding(end = 4.dp)
                )
            }
            Text(
                text = label,
                style = MaterialTheme.typography.labelMedium.copy(
                    fontWeight = FontWeight.SemiBold,
                    fontFamily = FontFamily.Monospace,
                    fontSize = 12.sp
                ),
                color = MaterialTheme.colorScheme.onSurface
            )
        }
    }
}

@Composable
private fun TactileArrowKey(
    icon: ImageVector,
    description: String,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(12.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.4f)),
        modifier = Modifier
            .size(54.dp)
            .semantics { contentDescription = description }
    ) {
        Box(contentAlignment = Alignment.Center) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(28.dp)
            )
        }
    }
}
