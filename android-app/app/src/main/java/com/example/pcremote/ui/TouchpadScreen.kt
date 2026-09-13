package com.example.pcremote.ui

import android.os.SystemClock
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowLeft
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.outlined.Gesture
import androidx.compose.material.icons.outlined.Grid4x4
import androidx.compose.material.icons.outlined.KeyboardReturn
import androidx.compose.material.icons.outlined.Mouse
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.ui.theme.RemoteColors
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Redesigned Touchpad Screen:
 * - Maximizes surface area for responsive, edge-to-edge mouse navigation
 * - Precision tactile trackpad with corner brackets & center reticle
 * - Modern segmented mode switcher (Trackpad vs D-Pad)
 * - Ergonomic split bottom click deck (wide primary left click + right click)
 * - Integrated right-edge scroll lane with physical spring back
 * - First-class presentation/media D-Pad mode with glide acceleration
 */
@Composable
fun TouchpadScreen(
    connection: RemoteConnection,
    sensitivity: Float = 1.5f,
    hapticsEnabled: Boolean = true,
    settingsStore: SettingsStore
) {
    var dpadMode by rememberSaveable { mutableStateOf(false) }
    val hintSeen by settingsStore.touchpadHintSeen.collectAsState()
    val haptics = LocalHapticFeedback.current

    fun triggerHaptic(type: HapticFeedbackType = HapticFeedbackType.LongPress) {
        if (hapticsEnabled) haptics.performHapticFeedback(type)
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 14.dp, vertical = 8.dp)
    ) {
        // Mode & Control Header Bar
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 8.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            // Sensitivity multiplier indicator badge
            Surface(
                color = MaterialTheme.colorScheme.surfaceContainer,
                shape = RoundedCornerShape(8.dp),
                modifier = Modifier.padding(vertical = 2.dp)
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    Icon(
                        imageVector = Icons.Outlined.Mouse,
                        contentDescription = null,
                        modifier = Modifier.size(13.dp),
                        tint = MaterialTheme.colorScheme.primary
                    )
                    Text(
                        text = "${((sensitivity * 10).roundToInt()) / 10f}\u00D7 Sens",
                        style = MaterialTheme.typography.labelSmall.copy(fontSize = 11.sp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            // Segmented Pill Mode Switcher: [ Trackpad | D-Pad ]
            Surface(
                color = MaterialTheme.colorScheme.surfaceContainerHigh,
                shape = RoundedCornerShape(20.dp),
                border = androidx.compose.foundation.BorderStroke(
                    1.dp,
                    MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)
                )
            ) {
                Row(
                    modifier = Modifier.padding(3.dp),
                    horizontalArrangement = Arrangement.spacedBy(2.dp)
                ) {
                    SegmentedPillTab(
                        selected = !dpadMode,
                        title = "Trackpad",
                        icon = Icons.Outlined.Gesture,
                        onClick = { dpadMode = false }
                    )
                    SegmentedPillTab(
                        selected = dpadMode,
                        title = "D-Pad",
                        icon = Icons.Outlined.Grid4x4,
                        onClick = { dpadMode = true }
                    )
                }
            }
        }

        // Primary Surface: Trackpad or D-Pad
        if (dpadMode) {
            DPadControlDeck(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                connection = connection,
                sensitivity = sensitivity,
                hapticsEnabled = hapticsEnabled,
                onFeedback = { triggerHaptic() }
            )
        } else {
            PrecisionTrackpadSurface(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                connection = connection,
                sensitivity = sensitivity,
                hapticsEnabled = hapticsEnabled,
                hintSeen = hintSeen,
                onDismissHint = { settingsStore.markTouchpadHintSeen() },
                onHaptic = { triggerHaptic() }
            )
        }

        // Ergonomic Bottom Click Zones (Left / Right split)
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            // Wide Primary Left Click (65% width)
            TactileClickButton(
                label = "Left Click",
                subtitle = "Primary action",
                isPrimary = true,
                modifier = Modifier.weight(0.65f),
                onClick = {
                    triggerHaptic()
                    connection.sendMouseClick(button = "left", action = "click")
                }
            )

            // Tactile Secondary Right Click (35% width)
            TactileClickButton(
                label = "Right Click",
                subtitle = "Context menu",
                isPrimary = false,
                modifier = Modifier.weight(0.35f),
                onClick = {
                    triggerHaptic()
                    connection.sendMouseClick(button = "right", action = "click")
                }
            )
        }
    }
}

@Composable
private fun SegmentedPillTab(
    selected: Boolean,
    title: String,
    icon: ImageVector,
    onClick: () -> Unit
) {
    val bg = if (selected) MaterialTheme.colorScheme.primary else Color.Transparent
    val fg = if (selected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant

    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(16.dp))
            .background(bg)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = fg,
            modifier = Modifier.size(14.dp)
        )
        Text(
            text = title,
            style = MaterialTheme.typography.labelMedium.copy(
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium
            ),
            color = fg
        )
    }
}

/**
 * Precision Trackpad Touch Area with tactical corner brackets, reticle,
 * and unified gesture engine.
 */
@Composable
private fun PrecisionTrackpadSurface(
    modifier: Modifier = Modifier,
    connection: RemoteConnection,
    sensitivity: Float,
    hapticsEnabled: Boolean,
    hintSeen: Boolean,
    onDismissHint: () -> Unit,
    onHaptic: () -> Unit
) {
    val outlineColor = MaterialTheme.colorScheme.outline.copy(alpha = 0.4f)
    val reticleColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)

    Box(
        modifier = modifier
            .clip(RoundedCornerShape(18.dp))
            .background(RemoteColors.SurfaceContainer)
            .border(1.dp, outlineColor, RoundedCornerShape(18.dp))
            .pointerInput(sensitivity) {
                val engine = TouchpadGestureEngine(
                    slopPx = viewConfiguration.touchSlop,
                    longPressTimeoutMs = viewConfiguration.longPressTimeoutMillis
                )

                fun perform(action: TouchpadGestureEngine.Action) {
                    when (action) {
                        is TouchpadGestureEngine.Action.MoveCursor -> {
                            val dx = (action.dx * sensitivity).roundToInt()
                            val dy = (action.dy * sensitivity).roundToInt()
                            if (dx != 0 || dy != 0) connection.sendMouseMove(dx, dy)
                        }
                        is TouchpadGestureEngine.Action.Scroll ->
                            connection.sendScroll(action.steps)
                        TouchpadGestureEngine.Action.LeftClick ->
                            connection.sendMouseClick(button = "left", action = "click")
                        TouchpadGestureEngine.Action.RightClick -> {
                            onHaptic()
                            connection.sendMouseClick(button = "right", action = "click")
                        }
                        TouchpadGestureEngine.Action.RightDown -> {
                            onHaptic()
                            connection.sendMouseClick(button = "right", action = "down")
                        }
                        TouchpadGestureEngine.Action.RightUp ->
                            connection.sendMouseClick(button = "right", action = "up")
                        TouchpadGestureEngine.Action.Haptic -> onHaptic()
                    }
                }

                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    if (down.isConsumed) return@awaitEachGesture
                    down.consume()
                    engine.down(down.uptimeMillis)

                    while (true) {
                        val wait = engine.waitMs(SystemClock.uptimeMillis())
                        val event = if (wait == null) awaitPointerEvent()
                        else withTimeoutOrNull(wait) { awaitPointerEvent() }

                        if (event == null) {
                            engine.tick(SystemClock.uptimeMillis()).forEach(::perform)
                            continue
                        }

                        val primary = event.changes.firstOrNull { it.id == down.id }
                            ?: event.changes.firstOrNull { it.pressed }
                        val pressedCount = event.changes.count { it.pressed }

                        if (primary == null) {
                            if (pressedCount == 0) break else continue
                        }

                        val actions = if (primary.pressed) {
                            val dy = event.changes
                                .filter { it.pressed }
                                .sumOf { it.positionChange().y.toDouble() }
                                .toFloat()
                            engine.move(
                                now = primary.uptimeMillis,
                                dx = primary.positionChange().x,
                                dy = dy,
                                pointerCount = pressedCount
                            )
                        } else {
                            engine.up(now = primary.uptimeMillis, pointerCount = pressedCount)
                        }
                        actions.forEach(::perform)
                        event.changes.forEach { if (it.pressed) it.consume() }

                        if (event.changes.none { it.pressed } && engine.isIdle()) break
                    }
                }
            },
        contentAlignment = Alignment.Center
    ) {
        // Tactical Technical Canvas (Corner brackets + center reticle)
        Canvas(modifier = Modifier.fillMaxSize()) {
            val stroke = 1.5.dp.toPx()
            val bracketLen = 20.dp.toPx()
            val pad = 16.dp.toPx()

            // Top-left bracket
            drawLine(reticleColor, Offset(pad, pad), Offset(pad + bracketLen, pad), stroke)
            drawLine(reticleColor, Offset(pad, pad), Offset(pad, pad + bracketLen), stroke)

            // Top-right bracket
            drawLine(reticleColor, Offset(size.width - pad - bracketLen, pad), Offset(size.width - pad, pad), stroke)
            drawLine(reticleColor, Offset(size.width - pad, pad), Offset(size.width - pad, pad + bracketLen), stroke)

            // Bottom-left bracket
            drawLine(reticleColor, Offset(pad, size.height - pad), Offset(pad + bracketLen, size.height - pad), stroke)
            drawLine(reticleColor, Offset(pad, size.height - pad - bracketLen), Offset(pad, size.height - pad), stroke)

            // Bottom-right bracket
            drawLine(reticleColor, Offset(size.width - pad - bracketLen, size.height - pad), Offset(size.width - pad, size.height - pad), stroke)
            drawLine(reticleColor, Offset(size.width - pad, size.height - pad - bracketLen), Offset(size.width - pad, size.height - pad), stroke)

            // Center subtle crosshair
            val cx = size.width / 2
            val cy = size.height / 2
            val reticleSize = 14.dp.toPx()
            drawLine(reticleColor, Offset(cx - reticleSize, cy), Offset(cx + reticleSize, cy), 1f)
            drawLine(reticleColor, Offset(cx, cy - reticleSize), Offset(cx, cy + reticleSize), 1f)
        }

        // Watermark Label
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            Text(
                text = "PRECISION TRACKPAD",
                style = MaterialTheme.typography.labelMedium.copy(
                    letterSpacing = 2.5.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.35f)
            )
            Text(
                text = "1-Finger Move · Tap Left Click · 2-Finger Scroll",
                style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.25f)
            )
        }

        // Integrated Right-Edge Scroll Lane
        ScrollTrack(
            modifier = Modifier
                .align(Alignment.CenterEnd)
                .fillMaxHeight()
                .width(44.dp)
                .padding(end = 6.dp, top = 20.dp, bottom = 20.dp),
            connection = connection,
            hapticsEnabled = hapticsEnabled
        )

        // First-Time Gesture Helper Pill (dismissible)
        if (!hintSeen) {
            GestureHintPill(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(14.dp),
                onDismiss = onDismissHint
            )
        }
    }
}

@Composable
private fun GestureHintPill(
    modifier: Modifier = Modifier,
    onDismiss: () -> Unit
) {
    Surface(
        color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f),
        shape = RoundedCornerShape(12.dp),
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)),
        modifier = modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(
                text = "💡 Tap = Left click  •  Hold = Right click  •  2 Fingers = Scroll",
                style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f)
            )
            IconButton(
                onClick = onDismiss,
                modifier = Modifier.size(28.dp)
            ) {
                Icon(
                    Icons.Filled.Close,
                    contentDescription = "Dismiss hint",
                    modifier = Modifier.size(16.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

/**
 * Tactile mouse click surfaces (Left / Right) with clear visual feedback.
 */
@Composable
private fun TactileClickButton(
    label: String,
    subtitle: String,
    isPrimary: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    val interaction = remember { MutableInteractionSource() }
    val isPressed by interaction.collectIsPressedAsState()

    val containerColor = when {
        isPressed && isPrimary -> MaterialTheme.colorScheme.primaryContainer
        isPressed -> MaterialTheme.colorScheme.surfaceContainerHigh
        isPrimary -> MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)
        else -> MaterialTheme.colorScheme.surfaceContainer
    }

    val borderColor = when {
        isPrimary -> MaterialTheme.colorScheme.primary.copy(alpha = 0.5f)
        else -> MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)
    }

    Surface(
        onClick = onClick,
        interactionSource = interaction,
        shape = RoundedCornerShape(14.dp),
        color = containerColor,
        border = androidx.compose.foundation.BorderStroke(1.dp, borderColor),
        modifier = modifier.height(60.dp)
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Text(
                text = label,
                style = MaterialTheme.typography.titleSmall.copy(
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 14.sp
                ),
                color = if (isPrimary) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface
            )
            Text(
                text = subtitle,
                style = MaterialTheme.typography.bodySmall.copy(fontSize = 11.sp),
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

/**
 * High-craft D-Pad Deck designed for TV, remote presentations, and menu navigation.
 */
@Composable
private fun DPadControlDeck(
    modifier: Modifier,
    connection: RemoteConnection,
    sensitivity: Float,
    hapticsEnabled: Boolean,
    onFeedback: () -> Unit
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(18.dp))
            .background(RemoteColors.SurfaceContainer)
            .border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.4f), RoundedCornerShape(18.dp))
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.SpaceBetween
        ) {
            // Deck Header
            Text(
                text = "DIRECTIONAL NAVIGATOR",
                style = MaterialTheme.typography.labelMedium.copy(
                    letterSpacing = 2.sp,
                    fontWeight = FontWeight.Medium
                ),
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
            )

            // D-Pad Cross Cluster
            DPadCrossCluster(
                connection = connection,
                sensitivity = sensitivity,
                onFeedback = onFeedback
            )

            // Utility Navigation Keys Row: ESC (Back) & ENTER (OK)
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.SpaceEvenly
            ) {
                OutlinedButton(
                    onClick = {
                        onFeedback()
                        connection.sendKey("ESC")
                    },
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.height(44.dp).width(110.dp)
                ) {
                    Text("ESC / Back", style = MaterialTheme.typography.labelMedium)
                }

                OutlinedButton(
                    onClick = {
                        onFeedback()
                        connection.sendKey("ENTER")
                    },
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.height(44.dp).width(110.dp)
                ) {
                    Icon(
                        Icons.Outlined.KeyboardReturn,
                        contentDescription = null,
                        modifier = Modifier.size(16.dp).padding(end = 4.dp)
                    )
                    Text("Enter", style = MaterialTheme.typography.labelMedium)
                }
            }
        }

        // Scroll track also accessible in D-pad mode
        ScrollTrack(
            modifier = Modifier
                .align(Alignment.CenterEnd)
                .fillMaxHeight()
                .width(44.dp)
                .padding(end = 6.dp, top = 20.dp, bottom = 20.dp),
            connection = connection,
            hapticsEnabled = hapticsEnabled
        )
    }
}

@Composable
private fun DPadCrossCluster(
    connection: RemoteConnection,
    sensitivity: Float,
    onFeedback: () -> Unit
) {
    val step = (28 * sensitivity).roundToInt().coerceAtLeast(8)
    val repeatStep = (10 * sensitivity).roundToInt().coerceAtLeast(3)

    suspend fun glide(dx: Int, dy: Int) {
        val chunks = 4
        repeat(chunks) {
            connection.sendMouseMove(dx / chunks, dy / chunks)
            delay(20)
        }
    }

    val padSize = 64.dp

    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        // UP
        TactileDirectionButton(
            size = padSize,
            icon = Icons.Filled.KeyboardArrowUp,
            description = "Move Up",
            onFeedback = onFeedback,
            onPress = { glide(0, -step) },
            onRepeat = { connection.sendMouseMove(0, -repeatStep) }
        )

        // LEFT, CENTER SELECT, RIGHT
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            TactileDirectionButton(
                size = padSize,
                icon = Icons.Filled.KeyboardArrowLeft,
                description = "Move Left",
                onFeedback = onFeedback,
                onPress = { glide(-step, 0) },
                onRepeat = { connection.sendMouseMove(-repeatStep, 0) }
            )

            // Center Select Disc (Left Click)
            Surface(
                onClick = {
                    onFeedback()
                    connection.sendMouseClick(button = "left", action = "click")
                },
                shape = CircleShape,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(padSize)
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Text(
                        text = "OK",
                        style = MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.Bold),
                        color = MaterialTheme.colorScheme.onPrimary
                    )
                }
            }

            TactileDirectionButton(
                size = padSize,
                icon = Icons.Filled.KeyboardArrowRight,
                description = "Move Right",
                onFeedback = onFeedback,
                onPress = { glide(step, 0) },
                onRepeat = { connection.sendMouseMove(repeatStep, 0) }
            )
        }

        // DOWN
        TactileDirectionButton(
            size = padSize,
            icon = Icons.Filled.KeyboardArrowDown,
            description = "Move Down",
            onFeedback = onFeedback,
            onPress = { glide(0, step) },
            onRepeat = { connection.sendMouseMove(0, repeatStep) }
        )
    }
}

@Composable
private fun TactileDirectionButton(
    size: Dp,
    icon: ImageVector,
    description: String,
    onFeedback: () -> Unit,
    onPress: suspend () -> Unit,
    onRepeat: () -> Unit
) {
    val interaction = remember { MutableInteractionSource() }
    val scope = rememberCoroutineScope()
    val pressed by interaction.collectIsPressedAsState()
    var handledByHold by remember { mutableStateOf(false) }
    var lastPressAt by remember { mutableStateOf(0L) }

    LaunchedEffect(interaction) {
        snapshotFlow { pressed }.collect { isPressedNow ->
            if (isPressedNow) {
                handledByHold = false
                lastPressAt = SystemClock.uptimeMillis()
                onPress()
                delay(220)
                if (pressed) {
                    handledByHold = true
                    while (pressed) {
                        onRepeat()
                        delay(50)
                    }
                }
            }
        }
    }

    Surface(
        onClick = {
            onFeedback()
            when {
                handledByHold -> handledByHold = false
                SystemClock.uptimeMillis() - lastPressAt > 300 -> scope.launch { onPress() }
            }
        },
        interactionSource = interaction,
        shape = RoundedCornerShape(16.dp),
        color = if (pressed) MaterialTheme.colorScheme.surfaceContainerHigh else MaterialTheme.colorScheme.surfaceVariant,
        border = androidx.compose.foundation.BorderStroke(
            1.dp,
            if (pressed) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = 0.3f)
        ),
        modifier = Modifier
            .size(size)
            .semantics { contentDescription = description }
    ) {
        Box(contentAlignment = Alignment.Center) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = if (pressed) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.size(30.dp)
            )
        }
    }
}

/**
 * Physical Spring Scroll Strip along the touchpad's right boundary.
 */
@Composable
private fun ScrollTrack(
    modifier: Modifier,
    connection: RemoteConnection,
    hapticsEnabled: Boolean
) {
    val haptics = LocalHapticFeedback.current
    val thumb = remember { Animatable(0f) }
    val scope = rememberCoroutineScope()
    val thumbHeight = 52.dp
    val scrollStep = 36.dp

    BoxWithConstraints(modifier) {
        val density = LocalDensity.current
        val travelPx = with(density) { ((maxHeight - thumbHeight) / 2).toPx() }
        val stepPx = with(density) { scrollStep.toPx() }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .pointerInput(travelPx, stepPx) {
                    awaitEachGesture {
                        val down = awaitFirstDown(requireUnconsumed = false)
                        down.consume()
                        var sent = 0f
                        var total = 0f
                        var announced = false
                        while (true) {
                            val event = awaitPointerEvent()
                            val change = event.changes.firstOrNull { it.pressed }
                            if (change == null) {
                                if (event.changes.none { it.pressed }) break else continue
                            }
                            total += change.positionChange().y
                            change.consume()
                            if (!announced && abs(total) > 4f) {
                                announced = true
                                if (hapticsEnabled) {
                                    haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                                }
                            }
                            scope.launch { thumb.snapTo((total / travelPx).coerceIn(-1f, 1f)) }
                            while (abs(total - sent) >= stepPx) {
                                val dir = if (total > sent) 1 else -1
                                connection.sendScroll(dir.coerceIn(-5, 5))
                                sent += dir * stepPx
                            }
                            if (event.changes.none { it.pressed }) break
                        }
                        scope.launch {
                            thumb.animateTo(0f, spring(stiffness = Spring.StiffnessMediumLow))
                        }
                    }
                }
        ) {
            // Track rail
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .fillMaxHeight()
                    .width(4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.15f))
            )

            // Animated thumb indicator
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .offset { IntOffset(0, (thumb.value * travelPx).roundToInt()) }
                    .width(6.dp)
                    .height(thumbHeight)
                    .clip(RoundedCornerShape(3.dp))
                    .background(MaterialTheme.colorScheme.primary)
            )
        }
    }
}
