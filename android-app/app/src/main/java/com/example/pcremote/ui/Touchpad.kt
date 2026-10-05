package com.example.pcremote.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.platform.LocalHapticFeedback
import com.example.pcremote.remote.Action
import com.example.pcremote.remote.TouchpadGestures

/**
 * The touchpad surface: everything the user does with a finger lands here.
 *
 * Gestures are recognised inline and forwarded immediately. Cursor movement must
 * never wait for a frame, a gesture recogniser or an animation, so movement
 * becomes a drag the moment it starts rather than after touch slop.
 */
@Composable
fun Touchpad(
    modifier: Modifier = Modifier,
    gestures: TouchpadGestures,
    haptics: Boolean = true,
    onAction: (Action) -> Unit,
) {
    val haptic = LocalHapticFeedback.current

    Box(
        modifier = modifier
            .background(Color(0xFF1C1C1E))
            .pointerInput(gestures) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val origin = down.position
                    val downAt = down.uptimeMillis
                    var furthest = 0f
                    var maxPointers = 1

                    gestures.onDown(origin.x, origin.y, 1)?.let(onAction)

                    while (true) {
                        val event = awaitPointerEvent()
                        val changes: List<PointerInputChange> = event.changes
                        val pressed = changes.count { it.pressed }

                        for (change in changes) {
                            furthest = maxOf(furthest, (change.position - origin).getDistance())
                            if (change.positionChanged()) change.consume()
                        }

                        // A second finger turns the gesture into a right click or
                        // a scroll, which the gesture logic decides from the count.
                        maxPointers = maxOf(maxPointers, pressed)

                        // The finger that started the gesture leads the movement.
                        val primary = changes.firstOrNull { it.id == down.id } ?: changes.first()
                        gestures.onMove(primary.position.x, primary.position.y, pressed)
                            ?.let { action ->
                                onAction(action)
                                // A tick on click and hold, not on scrolling: the
                                // finger is moving too much to feel anything.
                                if (haptics && (action is Action.LeftClick || action is Action.HoldEnd)) {
                                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                }
                            }

                        if (pressed == 0) {
                            val heldMs = changes.maxOf { it.uptimeMillis } - downAt
                            gestures.onUp(maxPointers, heldMs, furthest)?.let(onAction)
                            break
                        }
                    }
                }
            },
    )
}