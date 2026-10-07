package com.example.pcremote.remote

/**
 * Works out what to send to the PC when the user edits the text field.
 *
 * The field holds a running buffer, and the PC has to be told what changed - not
 * what the buffer now contains. Sending the whole value on every keystroke would
 * retype the entire sentence each time one character was appended, which is the
 * kind of bug that makes an app feel broken immediately.
 *
 * Pure logic so it can be tested directly.
 */
object TextDiff {

    /**
     * Returns the text to send, or null when nothing needs to go to the PC.
     *
     * `\b` (backspace) is used as a delete instruction, because the PC cannot
     * otherwise know a character was removed.
     */
    fun between(previous: String, updated: String): String? = when {
        updated.length > previous.length && updated.startsWith(previous) ->
            // Characters were appended: send only the new ones.
            updated.removePrefix(previous)

        updated.length < previous.length && previous.startsWith(updated) ->
            // Characters were deleted from the end: tell the PC to delete back.
            // The prefix test must be the PREVIOUS text, not the updated one -
            // a deletion from the middle (moving the cursor, then backspacing)
            // is not a prefix, and misreporting it as one would leave the PC's
            // text different from the phone's forever.
            "\b".repeat(previous.length - updated.length)

        updated == previous -> null

        // A replacement or a middle deletion has no natural incremental form,
        // so clear the line and retype it.
        else -> "\b".repeat(previous.length) + updated
    }
}