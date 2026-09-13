package com.example.pcremote.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.FastForward
import androidx.compose.material.icons.filled.FastRewind
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.VolumeDown
import androidx.compose.material.icons.filled.VolumeMute
import androidx.compose.material.icons.filled.VolumeOff
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.ui.theme.RemoteColors
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Production-ready Media Controller Console:
 * - High-craft visual hierarchy with dedicated playback & sound consoles
 * - Tactile hero transport disc (Play/Pause, Skip Next, Skip Previous)
 * - Hold-to-repeat volume controllers with instant mute toggle
 * - Fast-seek (5s scrub) and Fullscreen controls for YouTube, VLC, Netflix, Spotify
 * - Zero empty space: purposeful, ergonomic remote layout
 */
@Composable
fun MediaScreen(
    connection: RemoteConnection,
    hapticsEnabled: Boolean = true
) {
    var isMuted by remember { mutableStateOf(false) }
    var isPlayingEstimated by remember { mutableStateOf(true) }
    val haptics = LocalHapticFeedback.current

    fun performHaptic(type: HapticFeedbackType = HapticFeedbackType.LongPress) {
        if (hapticsEnabled) haptics.performHapticFeedback(type)
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        // --- 1. HERO PLAYBACK TRANSPORT DECK ---
        Surface(
            shape = RoundedCornerShape(20.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 20.dp, horizontal = 16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp)
            ) {
                Text(
                    text = "PLAYBACK CONTROLLER",
                    style = MaterialTheme.typography.labelSmall.copy(
                        letterSpacing = 1.5.sp,
                        fontWeight = FontWeight.SemiBold
                    ),
                    color = MaterialTheme.colorScheme.primary
                )

                // Main Transport Row: Prev | [PLAY / PAUSE HERO] | Next
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceEvenly,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // Skip Previous
                    TactileTransportButton(
                        icon = Icons.Filled.SkipPrevious,
                        label = "Previous Track",
                        size = 56.dp,
                        onClick = {
                            performHaptic()
                            connection.sendMedia("prev")
                        }
                    )

                    // Hero Play / Pause Disc (76dp)
                    HeroPlayPauseDisc(
                        isPlaying = isPlayingEstimated,
                        onClick = {
                            performHaptic()
                            isPlayingEstimated = !isPlayingEstimated
                            connection.sendMedia("play_pause")
                        }
                    )

                    // Skip Next
                    TactileTransportButton(
                        icon = Icons.Filled.SkipNext,
                        label = "Next Track",
                        size = 56.dp,
                        onClick = {
                            performHaptic()
                            connection.sendMedia("next")
                        }
                    )
                }

                // Secondary Seek & Transport Strip: -5s | Space | Stop | +5s | Fullscreen
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceEvenly,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // Seek Back 5s (Left Arrow)
                    MiniMediaTool(
                        icon = Icons.Filled.FastRewind,
                        label = "-5s",
                        onClick = {
                            performHaptic()
                            connection.sendKey("LEFT")
                        }
                    )

                    // Spacebar toggle
                    MiniMediaTool(
                        label = "SPACE",
                        onClick = {
                            performHaptic()
                            connection.sendKey("SPACE")
                        }
                    )

                    // Stop button
                    MiniMediaTool(
                        icon = Icons.Filled.Stop,
                        label = "Stop",
                        onClick = {
                            performHaptic()
                            connection.sendMedia("stop")
                            isPlayingEstimated = false
                        }
                    )

                    // Seek Forward 5s (Right Arrow)
                    MiniMediaTool(
                        icon = Icons.Filled.FastForward,
                        label = "+5s",
                        onClick = {
                            performHaptic()
                            connection.sendKey("RIGHT")
                        }
                    )

                    // Fullscreen Toggle ('F' key)
                    MiniMediaTool(
                        icon = Icons.Filled.Fullscreen,
                        label = "Full",
                        onClick = {
                            performHaptic()
                            connection.sendKey("F")
                        }
                    )
                }
            }
        }

        // --- 2. MASTER VOLUME CONTROLLER DECK ---
        Surface(
            shape = RoundedCornerShape(20.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 18.dp, horizontal = 16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "SYSTEM VOLUME",
                        style = MaterialTheme.typography.labelSmall.copy(
                            letterSpacing = 1.5.sp,
                            fontWeight = FontWeight.SemiBold
                        ),
                        color = MaterialTheme.colorScheme.primary
                    )
                    Text(
                        text = if (isMuted) "MUTED" else "ACTIVE",
                        style = MaterialTheme.typography.labelSmall.copy(
                            fontWeight = FontWeight.Bold,
                            fontSize = 10.sp
                        ),
                        color = if (isMuted) RemoteColors.Error else RemoteColors.Positive
                    )
                }

                // Volume Controls: Vol Down | Mute Toggle | Vol Up
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // Vol Down with Hold-to-Repeat
                    HoldRepeatVolumeButton(
                        icon = Icons.Filled.VolumeDown,
                        title = "Vol -",
                        modifier = Modifier.weight(1f),
                        onFeedback = { performHaptic(HapticFeedbackType.TextHandleMove) },
                        onRepeat = {
                            isMuted = false
                            connection.sendMedia("vol_down")
                        }
                    )

                    // Mute Button with toggle highlight
                    MuteToggleButton(
                        isMuted = isMuted,
                        modifier = Modifier.weight(0.9f),
                        onClick = {
                            performHaptic()
                            isMuted = !isMuted
                            connection.sendMedia("mute")
                        }
                    )

                    // Vol Up with Hold-to-Repeat
                    HoldRepeatVolumeButton(
                        icon = Icons.Filled.VolumeUp,
                        title = "Vol +",
                        modifier = Modifier.weight(1f),
                        onFeedback = { performHaptic(HapticFeedbackType.TextHandleMove) },
                        onRepeat = {
                            isMuted = false
                            connection.sendMedia("vol_up")
                        }
                    )
                }

                // Quick Volume Stepper Row
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(6.dp)
                ) {
                    QuickVolStepButton("Min (0%)", Modifier.weight(1f)) {
                        performHaptic()
                        repeat(25) { connection.sendMedia("vol_down") }
                    }
                    QuickVolStepButton("Step -5", Modifier.weight(1f)) {
                        performHaptic()
                        repeat(5) { connection.sendMedia("vol_down") }
                    }
                    QuickVolStepButton("Step +5", Modifier.weight(1f)) {
                        performHaptic()
                        repeat(5) { connection.sendMedia("vol_up") }
                    }
                    QuickVolStepButton("Max (100%)", Modifier.weight(1.1f)) {
                        performHaptic()
                        repeat(25) { connection.sendMedia("vol_up") }
                    }
                }
            }
        }

        // --- 3. PRESENTATION & VIDEO SHORTCUTS DECK ---
        Surface(
            shape = RoundedCornerShape(16.dp),
            color = MaterialTheme.colorScheme.surfaceContainer,
            border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Text(
                    text = "VIDEO & PRESENTATION SHORTCUTS",
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
                    AppMediaShortcut("Exit Full (ESC)", Modifier.weight(1f)) {
                        performHaptic()
                        connection.sendKey("ESC")
                    }
                    AppMediaShortcut("Subtitles (C)", Modifier.weight(1f)) {
                        performHaptic()
                        connection.sendKey("C")
                    }
                    AppMediaShortcut("Mute Audio (M)", Modifier.weight(1f)) {
                        performHaptic()
                        connection.sendKey("M")
                    }
                }
            }
        }
    }
}

/**
 * 76dp glowing circular disc for Play/Pause hero button.
 */
@Composable
private fun HeroPlayPauseDisc(
    isPlaying: Boolean,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = CircleShape,
        color = MaterialTheme.colorScheme.primary,
        shadowElevation = 6.dp,
        modifier = Modifier
            .size(76.dp)
            .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), CircleShape)
            .semantics { contentDescription = if (isPlaying) "Pause playback" else "Start playback" }
    ) {
        Box(contentAlignment = Alignment.Center) {
            Icon(
                imageVector = if (isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onPrimary,
                modifier = Modifier.size(38.dp)
            )
        }
    }
}

@Composable
private fun TactileTransportButton(
    icon: ImageVector,
    label: String,
    size: Dp,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = CircleShape,
        color = MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)),
        modifier = Modifier
            .size(size)
            .semantics { contentDescription = label }
    ) {
        Box(contentAlignment = Alignment.Center) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.size(26.dp)
            )
        }
    }
}

@Composable
private fun MiniMediaTool(
    label: String,
    icon: ImageVector? = null,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
        modifier = Modifier.height(38.dp)
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            if (icon != null) {
                Icon(
                    imageVector = icon,
                    contentDescription = label,
                    tint = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.size(16.dp)
                )
            }
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall.copy(fontWeight = FontWeight.SemiBold),
                color = MaterialTheme.colorScheme.onSurface
            )
        }
    }
}

/**
 * Precision Volume button that fires once on tap, then repeats every 150ms while held.
 */
@Composable
private fun HoldRepeatVolumeButton(
    icon: ImageVector,
    title: String,
    modifier: Modifier = Modifier,
    onFeedback: () -> Unit,
    onRepeat: () -> Unit
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    var handledByHold by remember { mutableStateOf(false) }

    LaunchedEffect(interaction) {
        snapshotFlow { pressed }.collect { isPressedNow ->
            if (isPressedNow) {
                delay(260)
                if (pressed) {
                    handledByHold = true
                    while (pressed) {
                        onFeedback()
                        onRepeat()
                        delay(140)
                    }
                }
            }
        }
    }

    Surface(
        onClick = {
            onFeedback()
            if (!handledByHold) onRepeat()
            handledByHold = false
        },
        interactionSource = interaction,
        shape = RoundedCornerShape(14.dp),
        color = if (pressed) MaterialTheme.colorScheme.surfaceContainerHigh else MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(
            1.dp,
            if (pressed) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)
        ),
        modifier = modifier
            .height(56.dp)
            .semantics { contentDescription = title }
    ) {
        Row(
            modifier = Modifier.fillMaxSize(),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(24.dp).padding(end = 6.dp)
            )
            Text(
                text = title,
                style = MaterialTheme.typography.labelMedium.copy(fontWeight = FontWeight.Bold),
                color = MaterialTheme.colorScheme.onSurface
            )
        }
    }
}

@Composable
private fun MuteToggleButton(
    isMuted: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    val bg = if (isMuted) RemoteColors.ErrorContainer.copy(alpha = 0.6f) else MaterialTheme.colorScheme.surfaceVariant
    val fg = if (isMuted) RemoteColors.Error else MaterialTheme.colorScheme.onSurface
    val border = if (isMuted) RemoteColors.Error else MaterialTheme.colorScheme.outline.copy(alpha = 0.35f)

    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(14.dp),
        color = bg,
        border = androidx.compose.foundation.BorderStroke(1.dp, border),
        modifier = modifier
            .height(56.dp)
            .semantics { contentDescription = if (isMuted) "Unmute audio" else "Mute audio" }
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Icon(
                imageVector = if (isMuted) Icons.Filled.VolumeOff else Icons.Filled.VolumeMute,
                contentDescription = null,
                tint = fg,
                modifier = Modifier.size(22.dp)
            )
            Text(
                text = if (isMuted) "MUTED" else "MUTE",
                style = MaterialTheme.typography.labelSmall.copy(
                    fontWeight = FontWeight.Bold,
                    fontSize = 10.sp
                ),
                color = fg
            )
        }
    }
}

@Composable
private fun QuickVolStepButton(
    label: String,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
        modifier = modifier.height(34.dp)
    ) {
        Box(contentAlignment = Alignment.Center, modifier = Modifier.fillMaxSize()) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall.copy(fontSize = 10.sp, fontWeight = FontWeight.Medium),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun AppMediaShortcut(
    label: String,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(10.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
        modifier = modifier.height(40.dp)
    ) {
        Box(contentAlignment = Alignment.Center, modifier = Modifier.fillMaxSize()) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall.copy(fontWeight = FontWeight.Medium),
                color = MaterialTheme.colorScheme.onSurface
            )
        }
    }
}
