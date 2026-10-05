package com.example.pcremote.remote

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * The rule these tests protect: the phone sends what CHANGED, not the whole
 * buffer. Sending the full value on every keystroke retypes the sentence on the
 * PC each time a character is added.
 */
class TextDiffTest {

    @Test
    fun `typing the first character sends just that character`() {
        assertEquals("h", TextDiff.between("", "h"))
    }

    @Test
    fun `appending a character sends only the new one`() {
        // The bug this guards: sending "hi" here would leave the PC showing "hhi".
        assertEquals("i", TextDiff.between("h", "hi"))
    }

    @Test
    fun `typing a word sends each character once`() {
        var previous = ""
        val sent = buildList {
            "hello".forEach { ch ->
                val next = previous + ch
                TextDiff.between(previous, next)?.let(::add)
                previous = next
            }
        }
        assertEquals("hello", sent.joinToString(""))
    }

    @Test
    fun `deleting a character sends one backspace`() {
        assertEquals("\b", TextDiff.between("hi", "h"))
    }

    @Test
    fun `deleting several characters sends one backspace each`() {
        assertEquals("\b\b\b", TextDiff.between("hello", "he"))
    }

    @Test
    fun `no change sends nothing at all`() {
        assertNull(TextDiff.between("hi", "hi"))
    }

    @Test
    fun `autocorrect rewrites the line rather than appending to it`() {
        // The PC and the phone must agree, so clear then retype.
        assertEquals("\b\b" + "H ", TextDiff.between("hh", "H "))
    }

    @Test
    fun `clearing the field sends one backspace per character`() {
        assertEquals("\b\b\b", TextDiff.between("abc", ""))
    }
}