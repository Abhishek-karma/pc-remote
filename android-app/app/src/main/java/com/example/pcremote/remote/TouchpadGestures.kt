package com.example.pcremote.remote

import kotlin.math.hypot

/** What a touchpad gesture asks the caller to do. */
sealed interface Action {
    /** Move the cursor by this many pixels. */
    data class Move(val dx: Float, val dy: Float) : Action

    /** Scroll by this many notches; positive scrolls up. */
    data class Scroll(val notches: Int) : Action

    data object LeftClick : Action
    data object RightClick : Action

    /** The left button went down and stays down. */
    data object HoldStart : Action

    /** The held left button was released. */
    data object HoldEnd : Action
}

/**
 * Turns pointer events on the touchpad into mouse actions.
 *
 * The gestures are deliberately the four a touchpad actually has:
 *   one finger drag  -> move the cursor
 *   one finger tap   -> left click
 *   two finger tap   -> right click
 *   two finger drag  -> scroll
 * plus a long press that holds the left button for dragging.
 *
 * There is no gesture engine here: the work is a handful of comparisons, and a
 * framework around it would only hide them. Pure logic with no Android
 * dependencies, so it is unit-tested directly.
 */
class TouchpadGestures(
    /** Cursor pixels per touchpad pixel. */
    private val sensitivity: Float = 1.5f,
    /** Movement below this many pixels still counts as a tap. */
    private val tapSlopPx: Float = 24f,
    /** Movement past this many pixels rules out a tap. */
    private val dragThresholdPx: Float = 8f,
    /** Touchpad pixels of vertical travel per scroll notch. */
    private val scrollPxPerNotch: Float = 40f,
    /** How long a still finger rests before the button is held down. Read by
     *  the touchpad, which owns the clock for it. */
    val longPressMs: Long = 350,
) {
    private var holding = false
    private var multiTouch = false
    private var scrolled = false
    private var pendingTap = true
    private var lastX = 0f
    private var lastY = 0f
    private var travelled = 0f

    /** True while a long press is holding the left button down. */
    val isHolding: Boolean get() = holding

    /** Resets after a gesture ends or is interrupted (a call, a system gesture). */
    fun reset() {
        holding = false
        multiTouch = false
        scrolled = false
        pendingTap = false
        travelled = 0f
    }

    /**
     * A finger went down. [pointers] is how many are on the pad, including this
     * one. [heldMs] is how long this finger has been down.
     */
    fun onDown(x: Float, y: Float, pointers: Int, heldMs: Long = 0): Action? {
        lastX = x
        lastY = y
        travelled = 0f
        scrolled = false
        multiTouch = pointers >= 2
        pendingTap = pointers < 2

        return when {
            multiTouch -> null // decided on lift, or by scrolling
            heldMs >= longPressMs -> {
                pendingTap = false
                holding = true
                Action.HoldStart
            }
            else -> null
        }
    }

    /**
     * The finger has been down and still for [longPressMs]. Decided by the
     * gesture state, not the clock: a finger that moved (pendingTap already
     * cleared) or a second finger on the pad (multiTouch) is not a hold.
     */
    fun onLongPress(): Action? {
        if (multiTouch || holding || !pendingTap) return null
        pendingTap = false
        holding = true
        return Action.HoldStart
    }

    /** A finger moved. [pointers] is how many are down now. */
    fun onMove(x: Float, y: Float, pointers: Int): Action? {
        val dx = x - lastX
        val dy = y - lastY
        lastX = x
        lastY = y
        travelled += hypot(dx, dy)

        // Decide this BEFORE moving the cursor, or a tap always nudges it.
        if (pendingTap && travelled > dragThresholdPx) pendingTap = false

        // A second finger can land at any point in the gesture, so the gesture
        // only becomes multi-touch here.
        if (pointers >= 2) {
            multiTouch = true
            pendingTap = false

            // Natural scrolling: dragging the fingers up scrolls content up.
            val notches = (-dy / scrollPxPerNotch).toInt()
            if (notches != 0) {
                scrolled = true
                return Action.Scroll(notches)
            }
            return null
        }

        // A tap in progress must not nudge the cursor. A HOLD, by contrast,
        // exists to drag: movement is the point, and the cursor must follow.
        if (pendingTap) return null

        return if (dx != 0f || dy != 0f) Action.Move(dx * sensitivity, dy * sensitivity) else null
    }

    /** Every finger lifted. [maxMovement] is the furthest any finger strayed. */
    fun onUp(pointers: Int, durationMs: Long, maxMovement: Float): Action? {
        if (holding) {
            holding = false
            return Action.HoldEnd
        }

        // Two fingers that went down but never scrolled: a right click.
        if (multiTouch && !scrolled) return Action.RightClick

        if (pendingTap && maxMovement <= tapSlopPx) {
            pendingTap = false
            return Action.LeftClick
        }

        pendingTap = false
        return null
    }
}