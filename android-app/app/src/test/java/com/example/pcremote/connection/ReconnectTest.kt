package com.example.pcremote.connection

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Backoff is pure arithmetic, so it is pinned here: a too-eager reconnect
 * hammers a PC that is merely asleep, and a too-slow one feels broken.
 */
class ReconnectTest {

    @Test
    fun `delay grows with each attempt`() {
        assertEquals(1_000L, Reconnect.delayMs(1))
        assertEquals(2_000L, Reconnect.delayMs(2))
        assertEquals(4_000L, Reconnect.delayMs(3))
        assertEquals(8_000L, Reconnect.delayMs(4))
    }

    @Test
    fun `delay is capped so a long outage still retries`() {
        assertEquals(30_000L, Reconnect.delayMs(20))
        assertEquals(30_000L, Reconnect.delayMs(200))
    }

    @Test
    fun `a silly attempt number cannot overflow into a negative delay`() {
        assertTrue(Reconnect.delayMs(1_000) > 0)
    }
}