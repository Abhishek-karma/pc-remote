package com.example.pcremote.ui

import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Pure gesture-classification state machine for the touchpad. The composable
 * feeds it pointer events; it returns the mouse actions to dispatch. Free of
 * Compose types so the tap/drag/long-press/scroll decisions — including the
 * late-pointer-delivery cases that caused accidental right-clicks — are
 * unit-testable on the JVM.
 *
 * Rules:
 *  - Movement is cumulative against [slopPx]; per-event jitter never flips a
 *    tap into a drag.
 *  - Two or more pressed pointers make the gesture a scroll for its whole
 *    lifetime (and cancel any pending long-press).
 *  - A click's type is decided from the pointer event's own timestamp at
 *    pointer-up, never from wall-clock timing: if events are delivered late
 *    (main-thread jank), a quick tap still classifies as a tap.
 *  - Long-press haptic fires at the wall-clock timeout, but the right button
 *    is only pressed after a further [holdConfirmMs] of continued stillness,
 *    so a genuinely quick tap never produces a right-down.
 */
class TouchpadGestureEngine(
    private val slopPx: Float,
    private val longPressTimeoutMs: Long,
    private val holdConfirmMs: Long = 150,
    private val scrollStepPx: Float = 48f,
    private val maxScrollSteps: Int = 5
) {
    sealed interface Action {
        /** Raw pointer delta in px; the caller applies sensitivity. */
        data class MoveCursor(val dx: Float, val dy: Float) : Action
        data class Scroll(val steps: Int) : Action
        object LeftClick : Action
        object RightClick : Action
        object RightDown : Action
        object RightUp : Action
        object Haptic : Action
    }

    private enum class State { Idle, Pending, Drag, Scroll, RightHold }

    private var state = State.Idle
    private var downTime = 0L
    private var totalMove = 0f
    private var scrollAccum = 0f
    private var longPressAnnounced = false

    fun isIdle() = state == State.Idle

    /** How long the caller may wait for the next pointer event; null = block. */
    fun waitMs(now: Long): Long? = when (state) {
        State.Idle, State.Drag, State.Scroll, State.RightHold -> null
        State.Pending -> {
            val deadline = downTime + longPressTimeoutMs +
                (if (longPressAnnounced) holdConfirmMs else 0L)
            (deadline - now).coerceAtLeast(0L)
        }
    }

    fun down(now: Long) {
        state = State.Pending
        downTime = now
        totalMove = 0f
        scrollAccum = 0f
        longPressAnnounced = false
    }

    /** [pointerCount] includes the primary pointer. [dx]/[dy] are its delta. */
    fun move(now: Long, dx: Float, dy: Float, pointerCount: Int): List<Action> {
        if (state == State.Idle) return emptyList()

        if (state != State.Scroll && pointerCount >= 2) {
            // Any second finger turns the gesture into a scroll, cancelling a
            // pending long-press. Its delta already counts toward scrolling.
            state = State.Scroll
            scrollAccum += dy
            return listOf(Action.Haptic)
        }

        return when (state) {
            State.Scroll -> {
                scrollAccum += dy
                if (abs(scrollAccum) >= scrollStepPx) {
                    val steps = (scrollAccum / scrollStepPx).roundToInt()
                        .coerceIn(-maxScrollSteps, maxScrollSteps)
                    scrollAccum -= steps * scrollStepPx
                    listOf(Action.Scroll(steps))
                } else emptyList()
            }
            State.Drag -> {
                if (dx != 0f || dy != 0f) listOf(Action.MoveCursor(dx, dy)) else emptyList()
            }
            State.RightHold -> emptyList() // right button held; moves are consumed
            State.Pending -> {
                totalMove += abs(dx) + abs(dy)
                when {
                    totalMove > slopPx -> {
                        state = State.Drag
                        if (dx != 0f || dy != 0f) listOf(Action.MoveCursor(dx, dy)) else emptyList()
                    }
                    now - downTime >= longPressTimeoutMs -> {
                        // Still genuinely held past the deadline, judged by
                        // the move's own timestamp (no wall clock, no
                        // announcement required) → press the right button
                        // (enables long-press right-drag).
                        state = State.RightHold
                        listOf(Action.RightDown)
                    }
                    else -> emptyList()
                }
            }
            State.Idle -> emptyList()
        }
    }

    /** [pointerCount] is how many pointers remain pressed after this one lifts. */
    fun up(now: Long, pointerCount: Int): List<Action> {
        val result = when (state) {
            State.Pending ->
                // Decide from the event's own timestamp, not wall clock —
                // this is what keeps jank-delayed taps from becoming
                // right-clicks.
                if (now - downTime >= longPressTimeoutMs) listOf(Action.RightClick)
                else listOf(Action.LeftClick)
            State.RightHold -> if (pointerCount == 0) listOf(Action.RightUp) else emptyList()
            else -> emptyList()
        }
        if (pointerCount == 0 || state == State.Pending) state = State.Idle
        return result
    }

    /** Called when no pointer event arrived within [waitMs]. [now] is wall clock. */
    fun tick(now: Long): List<Action> {
        if (state != State.Pending) return emptyList()
        val elapsed = now - downTime
        return when {
            !longPressAnnounced && elapsed >= longPressTimeoutMs -> {
                longPressAnnounced = true
                listOf(Action.Haptic)
            }
            longPressAnnounced && elapsed >= longPressTimeoutMs + holdConfirmMs -> {
                state = State.RightHold
                listOf(Action.RightDown)
            }
            else -> emptyList()
        }
    }
}
