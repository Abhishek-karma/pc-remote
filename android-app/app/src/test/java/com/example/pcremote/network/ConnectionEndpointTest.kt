package com.example.pcremote.network

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * Guards the URL-safety rule in ConnectionEndpoint. OkHttp throws
 * IllegalArgumentException from Request.Builder().url() for a host it cannot
 * parse, which used to crash the app straight out of a click handler.
 */
class ConnectionEndpointTest {

    @Test
    fun `accepts ordinary hosts and ipv4 literals`() {
        assertTrue(ConnectionEndpoint.hostIsValid("192.168.1.42"))
        assertTrue(ConnectionEndpoint.hostIsValid("10.0.0.1"))
        assertTrue(ConnectionEndpoint.hostIsValid("desktop"))
        assertTrue(ConnectionEndpoint.hostIsValid("my-pc.local"))
        assertTrue(ConnectionEndpoint.hostIsValid("  192.168.1.42  ".trim()))
    }

    @Test
    fun `rejects hosts okhttp cannot build a url from`() {
        // None of these can reach Request.Builder().url() any more.
        assertFalse(ConnectionEndpoint.hostIsValid(""))
        assertFalse(ConnectionEndpoint.hostIsValid("   "))
        assertFalse(ConnectionEndpoint.hostIsValid("my pc"))            // space
        assertFalse(ConnectionEndpoint.hostIsValid("192.168.1.1 58642")) // pasted ip + port
        assertFalse(ConnectionEndpoint.hostIsValid("http://192.168.1.1"))
        assertFalse(ConnectionEndpoint.hostIsValid("192.168.1.1/path"))
        assertFalse(ConnectionEndpoint.hostIsValid("0.0.0.0:58642"))     // colon (ipv6/port)
        assertFalse(ConnectionEndpoint.hostIsValid("a".repeat(254)))
    }

    @Test
    fun `rejects ports outside tcp range`() {
        assertTrue(ConnectionEndpoint.portIsValid(1))
        assertTrue(ConnectionEndpoint.portIsValid(ConnectionEndpoint.DEFAULT_PORT))
        assertTrue(ConnectionEndpoint.portIsValid(65535))
        assertFalse(ConnectionEndpoint.portIsValid(0))
        assertFalse(ConnectionEndpoint.portIsValid(70000))
        assertFalse(ConnectionEndpoint.portIsValid(-1))
    }

    @Test
    fun `defaults to the agent port`() {
        assertEquals(58642, ConnectionEndpoint.DEFAULT_PORT)
    }
}
