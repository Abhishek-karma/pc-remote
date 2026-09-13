package com.example.pcremote.ui

import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Adjust
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowLeft
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.ui.theme.Corners
import kotlin.math.roundToInt
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
                // Decorative watermark; real instructions live in the first-use hint.
                Icon(
                    Icons.Filled.Mouse,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.25f),
                    modifier = Modifier.size(44.dp)
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
 * Button-based cursor control — the TalkBack-accessible alternative path.
 * Every control: icon + content description + 56dp target (13 §2).
 */
@Composable
private fun DPadSurface(
    modifier: Modifier,
    connection: RemoteConnection,
    sensitivity: Float,
    onFeedback: () -> Unit
) {
    val step = (60 * sensitivity).roundToInt().coerceAtLeast(8)

    @Composable
    fun dpadButton(icon: ImageVector, description: String, onClick: () -> Unit) =
        androidx.compose.material3.OutlinedButton(
            onClick = {
                onFeedback()
                onClick()
            },
            modifier = Modifier
                .height(56.dp)
                .semantics { contentDescription = description }
        ) {
            Icon(icon, contentDescription = null)
        }

    Column(
        modifier = modifier
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(Corners.large))
            .padding(16.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Row { dpadButton(Icons.Filled.KeyboardArrowUp, "Move cursor up") { connection.sendMouseMove(0, -step) } }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            dpadButton(Icons.Filled.KeyboardArrowLeft, "Move cursor left") { connection.sendMouseMove(-step, 0) }
            dpadButton(Icons.Filled.Adjust, "Left click") { connection.sendMouseClick() }
            dpadButton(Icons.Filled.KeyboardArrowRight, "Move cursor right") { connection.sendMouseMove(step, 0) }
        }
        Row { dpadButton(Icons.Filled.KeyboardArrowDown, "Move cursor down") { connection.sendMouseMove(0, step) } }
    }
}
