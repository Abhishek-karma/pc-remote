package com.example.pcremote.remote

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The touchpad is the product, so its gesture rules are pinned down here rather
 * than discovered on a real desk.
 */
class TouchpadGesturesTest {

    private fun gestures() = TouchpadGestures(sensitivity = 1.5f)

    @Test
    fun `a still single finger is a left click`() {
        val g = gestures()
        g.onDown(100f, 100f, pointers = 1)
        val action = g.onUp(pointers = 1, durationMs = 80, maxMovement = 3f)
        assertEquals(Action.LeftClick, action)
    }

    @Test
    fun `a two finger tap is a right click`() {
        val g = gestures()
        g.onDown(100f, 100f, pointers = 1)
        g.onMove(100f, 100f, pointers = 2) // second finger landed
        val action = g.onUp(pointers = 2, durationMs = 80, maxMovement = 2f)
        assertEquals(Action.RightClick, action)
    }

    @Test
    fun `dragging one finger moves the cursor`() {
        val g = gestures()
        g.onDown(100f, 100f, pointers = 1)
        val action = g.onMove(140f, 100f, pointers = 1)
        // 40 touchpad px at sensitivity 1.5
        assertEquals(Action.Move(60f, 0f), action)
    }

    @Test
    fun `a tap does not nudge the cursor`() {
        // The bug this guards: recognising the tap only after moving, so every
        // tap shifted the pointer a few pixels.
        val g = gestures()
        g.onDown(100f, 100f, pointers = 1)
        assertNull(g.onMove(101f, 100f, pointers = 1))
        assertEquals(Action.LeftClick, g.onUp(1, 60, 1f))
    }

    @Test
    fun `a long press holds the button down and releasing ends the drag`() {
        val g = gestures()
        assertEquals(Action.HoldStart, g.onDown(100f, 100f, pointers = 1, heldMs = 400))
        assertTrue(g.isHolding)

        // A held drag must not also move the cursor.
        assertNull(g.onMove(160f, 100f, pointers = 1))
        assertEquals(Action.HoldEnd, g.onUp(1, 900, 60f))
        assertTrue(!g.isHolding)
    }

    @Test
    fun `a two finger drag scrolls instead of moving the cursor`() {
        val g = gestures()
        g.onDown(100f, 300f, pointers = 2)
        val action = g.onMove(100f, 220f, pointers = 2)
        // Natural scrolling: fingers moving UP scrolls content UP.
        assertIs<Action.Scroll>(action)
        assertEquals(2, action.notches)
    }

    @Test
    fun `a long drag is a drag, not a click`() {
        val g = gestures()
        g.onDown(100f, 100f, pointers = 1)
        g.onMove(180f, 100f, pointers = 1)
        assertNull(g.onUp(1, 500, 80f))
    }

    @Test
    fun `sensitivity scales the cursor delta`() {
        val slow = TouchpadGestures(sensitivity = 1f)
        slow.onDown(0f, 0f, pointers = 1)
        assertEquals(Action.Move(50f, 0f), slow.onMove(50f, 0f, pointers = 1))
    }
}