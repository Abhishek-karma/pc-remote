package com.example.pcremote.ui

import com.example.pcremote.ui.TouchpadGestureEngine.Action
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * Edge cases for the touchpad classification state machine — especially the
 * production bug where a quick tap could be misclassified as a right-click
 * when pointer events were delivered late.
 */
class TouchpadGestureEngineTest {

    private companion object {
        const val SLOP = 8f
        const val LONG_PRESS = 500L
        const val HOLD_CONFIRM = 150L
        const val DOWN = 1000L
    }

    private fun engine() = TouchpadGestureEngine(
        slopPx = SLOP,
        longPressTimeoutMs = LONG_PRESS,
        holdConfirmMs = HOLD_CONFIRM
    )

    private fun List<Action>.ofType(vararg types: Class<out Action>) =
        filter { it.javaClass in types }

    @Test
    fun `quick tap is a left click`() {
        val e = engine()
        e.down(DOWN)
        assertEquals(listOf(Action.LeftClick), e.up(DOWN + 120, pointerCount = 0))
    }

    @Test
    fun `tap whose up event arrives late is still a left click`() {
        // Main-thread jank: the long-press wall-clock deadline passes before
        // the finger's (already-lifted) up event is processed. The up event's
        // own timestamp must decide.
        val e = engine()
        e.down(DOWN)
        assertEquals(listOf(Action.Haptic), e.tick(DOWN + LONG_PRESS + 1))
        assertEquals(listOf(Action.LeftClick), e.up(DOWN + 140, pointerCount = 0))
    }

    @Test
    fun `tap with sub-slop jitter is a left click`() {
        val e = engine()
        e.down(DOWN)
        // 7 separate 1px jitters: cumulative 7px < slop, but never > slop per event.
        repeat(7) { i ->
            assertTrue(e.move(DOWN + 10 + i, dx = 1f, dy = 0f, pointerCount = 1).isEmpty())
        }
        assertEquals(listOf(Action.LeftClick), e.up(DOWN + 150, pointerCount = 0))
    }

    @Test
    fun `movement past slop becomes a drag and sends no click`() {
        val e = engine()
        e.down(DOWN)
        // 10px > slop: transitions to drag and dispatches this delta.
        val transition = e.move(DOWN + 10, dx = 5f, dy = 5f, pointerCount = 1)
        assertEquals(1, transition.ofType(Action.MoveCursor::class.java).size)
        val actions = e.move(DOWN + 20, dx = 3f, dy = -2f, pointerCount = 1)
        assertEquals(1, actions.ofType(Action.MoveCursor::class.java).size)
        assertTrue(e.up(DOWN + 400, pointerCount = 0).isEmpty())
    }

    @Test
    fun `long-press hold presses and releases the right button`() {
        val e = engine()
        e.down(DOWN)
        assertEquals(listOf(Action.Haptic), e.tick(DOWN + LONG_PRESS))
        assertEquals(listOf(Action.RightDown), e.tick(DOWN + LONG_PRESS + HOLD_CONFIRM))
        assertEquals(listOf(Action.RightUp), e.up(DOWN + 900, pointerCount = 0))
    }

    @Test
    fun `release after the long-press deadline is a right click`() {
        val e = engine()
        e.down(DOWN)
        assertEquals(listOf(Action.Haptic), e.tick(DOWN + LONG_PRESS))
        assertEquals(listOf(Action.RightClick), e.up(DOWN + LONG_PRESS + 60, pointerCount = 0))
    }

    @Test
    fun `movement while the right button is held is a right-drag, not a cursor move`() {
        val e = engine()
        e.down(DOWN)
        e.tick(DOWN + LONG_PRESS)
        assertEquals(listOf(Action.RightDown), e.tick(DOWN + LONG_PRESS + HOLD_CONFIRM))
        assertTrue(e.move(DOWN + 700, dx = 30f, dy = 30f, pointerCount = 1).isEmpty())
        assertEquals(listOf(Action.RightUp), e.up(DOWN + 800, pointerCount = 0))
    }

    @Test
    fun `still-held moves past the deadline press the right button`() {
        // Finger never stops perfectly still — a sub-slop move whose event
        // timestamp is past the deadline confirms the long-press.
        val e = engine()
        e.down(DOWN)
        assertTrue(e.move(DOWN + 300, dx = 1f, dy = 0f, pointerCount = 1).isEmpty())
        val actions = e.move(DOWN + 510, dx = 1f, dy = 0f, pointerCount = 1)
        assertEquals(listOf(Action.RightDown), actions)
        assertEquals(listOf(Action.RightUp), e.up(DOWN + 600, pointerCount = 0))
    }

    @Test
    fun `two-finger drag scrolls in bounded steps`() {
        val e = engine()
        e.down(DOWN)
        assertEquals(listOf(Action.Haptic), e.move(DOWN + 10, dx = 0f, dy = -20f, pointerCount = 2))
        assertTrue(e.move(DOWN + 30, dx = 0f, dy = -20f, pointerCount = 2).isEmpty()) // accum -40
        assertEquals(listOf(Action.Scroll(-1)), e.move(DOWN + 50, dx = 0f, dy = -20f, pointerCount = 2)) // -60 → -1 step
        assertTrue(e.up(DOWN + 80, pointerCount = 0).isEmpty())
    }

    @Test
    fun `two-finger tap sends no click`() {
        val e = engine()
        e.down(DOWN)
        e.move(DOWN + 10, dx = 0f, dy = 1f, pointerCount = 2)
        assertTrue(e.up(DOWN + 100, pointerCount = 1).isEmpty())
        assertTrue(e.up(DOWN + 110, pointerCount = 0).isEmpty())
    }

    @Test
    fun `a second finger cancels a pending long-press`() {
        val e = engine()
        e.down(DOWN)
        assertEquals(listOf(Action.Haptic), e.tick(DOWN + LONG_PRESS))
        assertEquals(listOf(Action.Haptic), e.move(DOWN + 520, dx = 0f, dy = 0f, pointerCount = 2))
        // Further ticks must not press the right button once scrolling.
        assertTrue(e.tick(DOWN + LONG_PRESS + HOLD_CONFIRM + 100).isEmpty())
        assertTrue(e.up(DOWN + 600, pointerCount = 0).isEmpty())
    }

    @Test
    fun `waitMs walks the long-press then hold-confirm deadlines`() {
        val e = engine()
        e.down(DOWN)
        assertEquals(LONG_PRESS, e.waitMs(DOWN))
        e.tick(DOWN + LONG_PRESS) // announced
        assertEquals(HOLD_CONFIRM, e.waitMs(DOWN + LONG_PRESS))
        e.tick(DOWN + LONG_PRESS + HOLD_CONFIRM) // right pressed
        assertEquals(null, e.waitMs(DOWN + LONG_PRESS + HOLD_CONFIRM))
    }
}
