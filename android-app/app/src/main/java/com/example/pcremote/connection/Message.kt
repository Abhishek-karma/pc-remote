package com.example.pcremote.connection

import kotlinx.serialization.Serializable

/**
 * One protocol frame, matching the PC Remote service exactly.
 *
 * Field names are the wire format and must stay identical to the C# side; see
 * windows-agent/src/PcRemote.Core/Protocol/Message.cs. Renaming one silently
 * breaks input.
 */
@Serializable
data class Message(
    val v: Int = 1,
    val type: String,
    // handshake
    val token: String? = null,
    val code: String? = null,
    val pcName: String? = null,
    val pcId: String? = null,
    val reason: String? = null,
    val state: String? = null,
    // input
    val dx: Int? = null,
    val dy: Int? = null,
    val button: String? = null,
    val action: String? = null,
    val delta: Int? = null,
    val key: String? = null,
    val text: String? = null,
) {
    companion object {
        const val HELLO = "hello"
        const val MOVE = "move"
        const val BUTTON = "button"
        const val SCROLL = "scroll"
        const val KEY = "key"
        const val TEXT = "text"
        const val RELEASE_ALL = "release_all"
        const val DISCONNECT = "disconnect"

        const val WELCOME = "welcome"
        const val ERROR = "error"
    }
}

/** Every key the PC will accept. Anything else is rejected by the PC, so the app
 *  never offers it. Keep in step with Injector.Keys on the Windows side. */
object Keys {
    const val CTRL = "CTRL"
    const val ALT = "ALT"
    const val SHIFT = "SHIFT"
    const val WIN = "WIN"

    val MODIFIERS = listOf(CTRL, ALT, SHIFT, WIN)
}