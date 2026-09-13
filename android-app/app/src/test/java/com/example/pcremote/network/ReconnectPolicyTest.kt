package com.example.pcremote.network

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ReconnectPolicyTest {

    private val policy = ReconnectPolicy()

    @Test
    fun `backoff doubles from 1s and caps at 30s`() {
        assertEquals(1_000L, policy.delayMs(1))
        assertEquals(2_000L, policy.delayMs(2))
        assertEquals(4_000L, policy.delayMs(3))
        assertEquals(8_000L, policy.delayMs(4))
        assertEquals(16_000L, policy.delayMs(5))
        assertEquals(30_000L, policy.delayMs(6))
        assertEquals(30_000L, policy.delayMs(50))
    }

    @Test
    fun `gives up only after the cumulative ceiling`() {
        val cap = policy.maxDurationMs
        assertFalse(policy.shouldGiveUp(0))
        assertFalse(policy.shouldGiveUp(cap - 1))
        assertTrue(policy.shouldGiveUp(cap))
    }
}
