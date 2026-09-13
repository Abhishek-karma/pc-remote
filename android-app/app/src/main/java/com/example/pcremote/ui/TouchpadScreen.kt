package com.example.pcremote.ui

import android.os.SystemClock
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
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
import androidx.compose.material.icons.filled.Adjust
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowLeft
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.ui.theme.Corners
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Touchpad screen — the primary control surface. Single unified gesture
 * handler: one finger drags the cursor, tap = left click, long-press = right
 * click, two-finger drag = scroll. D-pad mode is the TalkBack-accessible
 * alternative (13-ACCESSIBILITY.md §2). Instructions are a first-use hint,
 * not permanent text (04 §4).
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
    fun haptic() {
        if (hapticsEnabled) haptics.performHapticFeedback(HapticFeedbackType.LongPress)
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
            horizontalArrangement = Arrangement.End
        ) {
            TextButton(onClick = { dpadMode = !dpadMode }) {
                Text(if (dpadMode) "Switch to gestures" else "D-pad mode")
            }
        }

        if (dpadMode) {
            DPadSurface(
                modifier = Modifier.fillMaxWidth().weight(1f),
                connection = connection,
                sensitivity = sensitivity,
                hapticsEnabled = hapticsEnabled,
                onFeedback = ::haptic
            )
        } else {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .background(
                            MaterialTheme.colorScheme.surfaceVariant,
                            RoundedCornerShape(Corners.large)
                        )
                        .border(
                            1.dp,
                            MaterialTheme.colorScheme.outline,
                            RoundedCornerShape(Corners.large)
                        )
                        .pointerInput(sensitivity) {
                        // Unified gesture handler; all classification lives in
                        // TouchpadGestureEngine (unit-tested) — this loop only
                        // translates pointer events and dispatches actions.
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
                                    haptic()
                                    connection.sendMouseClick(button = "right", action = "click")
                                }
                                TouchpadGestureEngine.Action.RightDown -> {
                                    haptic()
                                    connection.sendMouseClick(button = "right", action = "down")
                                }
                                TouchpadGestureEngine.Action.RightUp ->
                                    connection.sendMouseClick(button = "right", action = "up")
                                TouchpadGestureEngine.Action.Haptic -> haptic()
                            }
                        }

                        awaitEachGesture {
                            val down = awaitFirstDown(requireUnconsumed = false)
                            // An overlay control (scroll track, hint button)
                            // may have claimed this gesture already — then it
                            // is not a cursor gesture.
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
                Text(
                    "TOUCHPAD",
                    style = MaterialTheme.typography.labelMedium,
                    letterSpacing = 3.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.45f)
                )
                ScrollTrack(
                    modifier = Modifier
                        .align(Alignment.CenterEnd)
                        .fillMaxHeight()
                        .width(40.dp)
                        .padding(horizontal = 8.dp, vertical = 18.dp),
                    connection = connection,
                    hapticsEnabled = hapticsEnabled
                )
                if (!hintSeen) {
                    GestureHint(
                        modifier = Modifier.align(Alignment.BottomCenter).padding(12.dp),
                        onDismiss = { settingsStore.markTouchpadHintSeen() }
                    )
                }
            }
        }

        // Click buttons — visually a split mouse.
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(64.dp)
                .padding(top = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            ClickSurface(
                label = "Left click",
                filled = true,
                modifier = Modifier.weight(1f),
                onClick = {
                    haptic()
                    connection.sendMouseClick(button = "left", action = "click")
                }
            )
            ClickSurface(
                label = "Right click",
                filled = false,
                modifier = Modifier.weight(1f),
                onClick = {
                    haptic()
                    connection.sendMouseClick(button = "right", action = "click")
                }
            )
        }
    }
}

@Composable
private fun GestureHint(modifier: Modifier = Modifier, onDismiss: () -> Unit) {
    androidx.compose.material3.Surface(
        color = MaterialTheme.colorScheme.surface.copy(alpha = 0.92f),
        shape = RoundedCornerShape(Corners.medium),
        tonalElevation = 2.dp,
        modifier = modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "Drag = move · Tap = click · Hold = right · Two fingers = scroll",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f)
            )
            TextButton(onClick = onDismiss) { Text("Got it") }
        }
    }
}

@Composable
private fun ClickSurface(label: String, filled: Boolean, modifier: Modifier = Modifier, onClick: () -> Unit) {
    androidx.compose.material3.Surface(
        onClick = onClick,
        shape = RoundedCornerShape(Corners.medium),
        color = if (filled) MaterialTheme.colorScheme.primary
        else MaterialTheme.colorScheme.surfaceVariant,
        modifier = modifier.semantics { contentDescription = label }
    ) {
        Box(contentAlignment = Alignment.Center, modifier = Modifier.fillMaxSize()) {
            Text(
                label,
                style = MaterialTheme.typography.labelLarge,
                color = if (filled) MaterialTheme.colorScheme.onPrimary
                else MaterialTheme.colorScheme.onSurface
            )
        }
    }
}

/**
 * Compact arrow cluster + click, used by the full-screen D-pad mode. Movement
 * glides: a quick tap nudges a short step, holding moves the cursor smoothly
 * and slowly in small continuous increments — never one big jump.
 */
@Composable
private fun DPadCluster(
    connection: RemoteConnection,
    sensitivity: Float,
    buttonSize: Dp,
    onFeedback: () -> Unit
) {
    val step = (24 * sensitivity).roundToInt().coerceAtLeast(6)
    val repeatStep = (8 * sensitivity).roundToInt().coerceAtLeast(2)

    // Sends [dx]/[dy] in small chunks with tiny pauses, so even a single tap
    // glides to its target instead of teleporting.
    suspend fun glide(dx: Int, dy: Int) {
        val chunks = 4
        repeat(chunks) {
            connection.sendMouseMove(dx / chunks, dy / chunks)
            delay(25)
        }
    }

    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Row {
            DPadButton(buttonSize, Icons.Filled.KeyboardArrowUp, "Move cursor up", onFeedback,
                onPress = { glide(0, -step) },
                onRepeat = { connection.sendMouseMove(0, -repeatStep) })
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            DPadButton(buttonSize, Icons.Filled.KeyboardArrowLeft, "Move cursor left", onFeedback,
                onPress = { glide(-step, 0) },
                onRepeat = { connection.sendMouseMove(-repeatStep, 0) })
            DPadButton(buttonSize, Icons.Filled.Adjust, "Left click", onFeedback,
                onPress = { connection.sendMouseClick() },
                onRepeat = { connection.sendMouseClick() })
            DPadButton(buttonSize, Icons.Filled.KeyboardArrowRight, "Move cursor right", onFeedback,
                onPress = { glide(step, 0) },
                onRepeat = { connection.sendMouseMove(repeatStep, 0) })
        }
        Row {
            DPadButton(buttonSize, Icons.Filled.KeyboardArrowDown, "Move cursor down", onFeedback,
                onPress = { glide(0, step) },
                onRepeat = { connection.sendMouseMove(0, repeatStep) })
        }
    }
}

@Composable
private fun DPadButton(
    size: Dp,
    icon: ImageVector,
    description: String,
    onFeedback: () -> Unit,
    onPress: suspend () -> Unit,
    onRepeat: () -> Unit
) {
    val interaction = remember { androidx.compose.foundation.interaction.MutableInteractionSource() }
    val scope = rememberCoroutineScope()
    val pressed by interaction.collectIsPressedAsState()
    var handledByHold by remember { mutableStateOf(false) }
    var lastPressAt by remember { mutableStateOf(0L) }
    // Hold-to-glide: the press animates its step in small chunks (no jump),
    // then a hold continues with small steps. A quick tap's glide keeps
    // running after release. onClick only acts when no real touch preceded it
    // (TalkBack activation) — otherwise the pressed flow already handled it.
    LaunchedEffect(interaction) {
        snapshotFlow { pressed }.collect { isPressedNow ->
            if (isPressedNow) {
                handledByHold = false
                lastPressAt = SystemClock.uptimeMillis()
                onPress()
                delay(250)
                if (pressed) {
                    handledByHold = true
                    while (pressed) {
                        onRepeat()
                        delay(60)
                    }
                }
            }
        }
    }
    androidx.compose.material3.OutlinedButton(
        onClick = {
            onFeedback()
            when {
                handledByHold -> handledByHold = false // hold already glided; consume the click
                SystemClock.uptimeMillis() - lastPressAt > 300 ->
                    scope.launch { onPress() } // no touch preceded — accessibility activation
            }
        },
        interactionSource = interaction,
        contentPadding = PaddingValues(0.dp),
        modifier = Modifier
            .size(size)
            .semantics { contentDescription = description }
    ) {
        Icon(icon, contentDescription = null, modifier = Modifier.size(size / 2))
    }
}

/**
 * Scroll track embedded in the touchpad's right edge (matches the product
 * mock): a thin recessed track with an amber thumb that follows the drag and
 * springs back to center on release. Drag up/down to scroll in fixed steps;
 * haptic on the first movement. Consumes its own pointer events so the pad
 * never treats a rail drag as cursor movement.
 */
@Composable
private fun ScrollTrack(
    modifier: Modifier,
    connection: RemoteConnection,
    hapticsEnabled: Boolean
) {
    val haptics = LocalHapticFeedback.current
    val thumb = remember { Animatable(0f) } // -1f..1f from track center
    val scope = rememberCoroutineScope()
    val thumbHeight = 56.dp
    val scrollStep = 40.dp
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
                        var sent = 0f // last step boundary crossed
                        var total = 0f // full drag distance, drives the thumb
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
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .fillMaxHeight()
                    .width(4.dp)
                    .background(
                        MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.18f),
                        RoundedCornerShape(2.dp)
                    )
            )
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .offset { IntOffset(0, (thumb.value * travelPx).roundToInt()) }
                    .width(5.dp)
                    .height(thumbHeight)
                    .background(
                        MaterialTheme.colorScheme.primary,
                        RoundedCornerShape(2.5.dp)
                    )
            )
        }
    }
}

/**
 * Full-screen D-pad mode — the TalkBack-accessible alternative path with
 * large 64dp targets (13 §2). The scroll track sits in exactly the same
 * spot as on the gesture pad: flush to the surface's right edge.
 */
@Composable
private fun DPadSurface(
    modifier: Modifier,
    connection: RemoteConnection,
    sensitivity: Float,
    hapticsEnabled: Boolean,
    onFeedback: () -> Unit
) {
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(Corners.large))) {
        Box(
            modifier = Modifier.fillMaxSize(),
            contentAlignment = Alignment.Center
        ) {
            DPadCluster(
                connection = connection,
                sensitivity = sensitivity,
                buttonSize = 64.dp,
                onFeedback = onFeedback
            )
        }
        ScrollTrack(
            modifier = Modifier
                .align(Alignment.CenterEnd)
                .fillMaxHeight()
                .width(40.dp)
                .padding(horizontal = 8.dp, vertical = 18.dp),
            connection = connection,
            hapticsEnabled = hapticsEnabled
        )
    }
}
