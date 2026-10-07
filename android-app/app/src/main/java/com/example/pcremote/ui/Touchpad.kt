package com.example.pcremote.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.PointerEvent
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.platform.LocalHapticFeedback
import com.example.pcremote.remote.Action
import com.example.pcremote.remote.TouchpadGestures
import kotlinx.coroutines.withTimeoutOrNull

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
                    var lastEventTime = downAt

                    fun dispatch(action: Action) {
                        // A tick on click and hold, not on scrolling or moving:
                        // the finger is moving too much to feel anything.
                        if (haptics && (action is Action.LeftClick ||
                                action is Action.HoldStart || action is Action.HoldEnd)
                        ) {
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        }
                        onAction(action)
                    }

                    gestures.onDown(origin.x, origin.y, 1)?.let(::dispatch)

                    // A finger that stays within HoldSlopPx for the gesture's
                    // long-press window is a long press and holds the left
                    // button. The window is measured from touch-down, not reset
                    // per event, so micro-jitter cannot postpone it forever —
                    // but a real drag, a lift or a second finger all produce a
                    // pointer event that cancels it first.
                    var holdPending = true

                    while (true) {
                        val event: PointerEvent = if (holdPending) {
                            val remaining = gestures.longPressMs - (lastEventTime - downAt)
                            val got = if (remaining > 0) {
                                withTimeoutOrNull(remaining) { awaitPointerEvent() }
                            } else {
                                null
                            }
                            if (got == null) {
                                holdPending = false
                                gestures.onLongPress()?.let(::dispatch)
                                continue
                            }
                            got
                        } else {
                            awaitPointerEvent()
                        }
                        val changes: List<PointerInputChange> = event.changes
                        val pressed = changes.count { it.pressed }
                        lastEventTime = maxOf(lastEventTime, changes.maxOf { it.uptimeMillis })

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
                            ?.let(::dispatch)

                        if (pressed == 0) {
                            val heldMs = lastEventTime - downAt
                            gestures.onUp(maxPointers, heldMs, furthest)?.let(::dispatch)
                            break
                        }

                        if (holdPending && (furthest > HoldSlopPx || maxPointers >= 2)) {
                            holdPending = false
                        }
                    }
                }
            },
    )
}

/** Touchpad travel beyond this many pixels rules out a long press. */
private const val HoldSlopPx = 20f