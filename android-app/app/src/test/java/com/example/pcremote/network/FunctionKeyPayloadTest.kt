package com.example.pcremote.network

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class FunctionKeyPayloadTest {

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = false }

    @Test
    fun `function key F1 payload encodes correctly`() {
        val msg = RemoteMessage(type = "key_press", key = "F1")
        val serialized = json.encodeToString(msg)
        assertTrue(serialized.contains("\"type\":\"key_press\""))
        assertTrue(serialized.contains("\"key\":\"F1\""))
    }

    @Test
    fun `function key F4 with ALT modifier encodes correctly`() {
        val msg = RemoteMessage(type = "key_press", key = "F4", modifiers = listOf("ALT"))
        val serialized = json.encodeToString(msg)
        assertTrue(serialized.contains("\"type\":\"key_press\""))
        assertTrue(serialized.contains("\"key\":\"F4\""))
        assertTrue(serialized.contains("\"modifiers\":[\"ALT\"]"))
    }

    @Test
    fun `function key F5 with CTRL modifier encodes correctly for hard reload`() {
        val msg = RemoteMessage(type = "key_press", key = "F5", modifiers = listOf("CTRL"))
        val serialized = json.encodeToString(msg)
        assertTrue(serialized.contains("\"type\":\"key_press\""))
        assertTrue(serialized.contains("\"key\":\"F5\""))
        assertTrue(serialized.contains("\"modifiers\":[\"CTRL\"]"))
    }

    @Test
    fun `all twelve function keys generate valid remote messages`() {
        (1..12).forEach { index ->
            val keyName = "F$index"
            val msg = RemoteMessage(type = "key_press", key = keyName)
            val decoded = json.decodeFromString<RemoteMessage>(json.encodeToString(msg))
            assertEquals("key_press", decoded.type)
            assertEquals(keyName, decoded.key)
        }
    }
}
