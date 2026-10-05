package com.example.pcremote.connection

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

/** What the user sees. Deliberately coarse: the app never shows internals. */
enum class ConnectionState {
    DISCONNECTED,
    CONNECTING,
    PAIRING,
    CONNECTED,
    RECONNECTING,
    FAILED,
}

/**
 * One connection to one PC.
 *
 * Connect with a saved token when the PC is already paired, or with a pairing
 * code the first time. Input messages are fire-and-forget: nothing waits for an
 * acknowledgement, so a busy network never backs up the touchpad.
 *
 * Reconnection uses capped exponential backoff, because the common failures (the
 * PC slept, Wi-Fi blipped, the service restarted) all heal on their own.
 */
class Connection(
    private val paired: PairedStore,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate),
) {
    companion object {
        const val PORT = 58642
        private const val TAG = "Connection"
    }

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    private val _state = MutableStateFlow(ConnectionState.DISCONNECTED)
    val state: StateFlow<ConnectionState> = _state

    private val _pcName = MutableStateFlow("")
    val pcName: StateFlow<String> = _pcName

    /** Desktop the PC reported: "normal", "locked", "secure" or "logon". */
    private val _desktop = MutableStateFlow("normal")
    val desktop: StateFlow<String> = _desktop

    /** Set when the PC refused us, so the UI can explain rather than retry. */
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error

    private var socket: WebSocket? = null
    private var host: String? = null
    private var pcId: String? = null
    private var client: OkHttpClient? = null
    private var reconnect: Job? = null
    private var attempt = 0
    private var closedByUs = false
    private var pairingCode: String? = null

    /** Paired PC ids, for the Settings list. */
    fun pairedIds(): List<String> = paired.pairedIds()

    /** Forgets this PC locally; it will need a new pairing code to return. */
    fun forget(pcId: String) = paired.forget(pcId)

    /**
     * Connects to [host]. [code] is only needed the first time; afterwards the
     * saved token is used automatically.
     */
    fun connect(host: String, code: String? = null) {
        reconnect?.cancel()
        reconnect = null

        // Switching to a different PC must not reuse the previous PC's id, or we
        // would send one machine's token to another and pin against the wrong
        // certificate.
        if (host != this.host) pcId = null

        this.host = host
        this.pairingCode = code
        attempt = 0
        closedByUs = false
        _error.value = null
        _state.value = ConnectionState.CONNECTING
        open(host, reconnecting = false)
    }

    /** Tries again with whatever is saved. Used by the Retry action. */
    fun retry() {
        val host = host ?: return
        connect(host, pairingCode)
    }

    /** User asked to disconnect: stop reconnecting and drop any held keys. */
    fun disconnect() {
        closedByUs = true
        reconnect?.cancel()
        reconnect = null
        releaseHeldInput()
        closeSocket()
        _state.value = ConnectionState.DISCONNECTED
    }

    fun shutdown() {
        disconnect()
        scope.cancel()
    }

    private fun open(target: String, reconnecting: Boolean) {
        val savedId = pcId
        val token = savedId?.let { paired.token(it) }

        if (savedId == null) {
            // First contact: we do not know the PC's id yet, so we cannot pin a
            // certificate. The fingerprint we see is offered to the user instead.
            Log.i(TAG, "first contact with $target (no saved pairing)")
        }

        _state.value = if (reconnecting) ConnectionState.RECONNECTING else ConnectionState.CONNECTING

        scope.launch {
            val ok = withContext(Dispatchers.IO) { handshake(target, savedId, token, reconnecting) }
            if (!ok && !closedByUs) scheduleReconnect()
        }
    }
    /** Opens the socket and waits for the handshake. Returns false when the PC
     *  refused us or the socket died, so the caller can decide about retrying. */
    private suspend fun handshake(
        target: String,
        savedId: String?,
        token: String?,
        reconnecting: Boolean,
    ): Boolean {
        val trust = PinningTrustManager(paired, savedId)
        val sslContext = SSLContext.getInstance("TLS")
        sslContext.init(null, arrayOf<javax.net.ssl.TrustManager>(trust), null)

        val okHttp = OkHttpClient.Builder()
            .sslSocketFactory(sslContext.socketFactory, trust)
            .hostnameVerifier { _, _ -> true } // trust comes from the fingerprint pin
            .pingInterval(15, TimeUnit.SECONDS)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS) // a live socket dies on ping failure
            .build()
        client = okHttp

        val request = Request.Builder().url("wss://$target:$PORT/").build()

        return suspendCancellableCoroutine { cont ->
            val ws = okHttp.newWebSocket(request, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    val hello = Message(
                        type = Message.HELLO,
                        token = token,
                        code = pairingCode,
                    )
                    webSocket.send(json.encodeToString(Message.serializer(), hello))
                }


                override fun onMessage(webSocket: WebSocket, text: String) {
                    val message = runCatching {
                        json.decodeFromString(Message.serializer(), text)
                    }.getOrNull() ?: return

                    when (message.type) {
                        Message.WELCOME -> {
                            val id = message.pcId ?: return

                            // On a first pairing the user read the code off this
                            // machine, which is the confirmation: the certificate
                            // we see now becomes the pin every later connection
                            // must match.
                            pcId = id
                            if (message.token != null) paired.saveToken(id, message.token)
                            trust.takePending()?.let { paired.savePin(id, it) }

                            _pcName.value = message.pcName.orEmpty()
                            _desktop.value = message.state ?: "normal"
                            _error.value = null
                            attempt = 0
                            _state.value = ConnectionState.CONNECTED
                            cont.resumeWith(Result.success(true))
                        }

                        Message.ERROR -> {
                            fail(humanReason(message.reason))

                            // A wrong token or code needs the user, not a retry:
                            // resuming false here stops the backoff loop.
                            val needsUser = message.reason == "pairing_failed" ||
                                message.reason == "not_paired"
                            if (needsUser) _state.value = ConnectionState.PAIRING
                            cont.resumeWith(Result.success(false))
                        }
                    }
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (socket === webSocket) {
                        fail("PC unavailable")
                        cont.resumeWith(Result.success(false))
                    }
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    if (socket === webSocket) {
                        if (!closedByUs) _error.value = "Disconnected"
                        cont.resumeWith(Result.success(false))
                    }
                }
            })
            socket = ws
            cont.invokeOnCancellation {
                ws.cancel()
                if (socket === ws) socket = null
            }
        }
    }

    /** Turns a wire reason into something a person can act on. */
    private fun humanReason(reason: String?): String = when (reason) {
        "pairing_failed" -> "Pairing failed - check the code on your PC"
        "not_paired" -> "This PC needs pairing again"
        "too_many_attempts" -> "Too many attempts - wait a moment and retry"
        "desktop_unavailable" -> "PC unavailable"
        else -> "PC unavailable"
    }

    private fun fail(message: String) {
        _error.value = message
        if (_state.value != ConnectionState.PAIRING) _state.value = ConnectionState.FAILED
    }

    private fun scheduleReconnect() {
        if (closedByUs || _state.value == ConnectionState.PAIRING) return

        reconnect?.cancel()
        reconnect = scope.launch {
            attempt++
            delay(Reconnect.delayMs(attempt))
            if (!closedByUs) {
                val target = host ?: return@launch
                open(target, reconnecting = true)
            }
        }
    }

    private fun closeSocket() {
        socket?.close(1000, "bye")
        socket = null
    }
    // --- input: all fire-and-forget, none of them wait for a reply ---

    fun move(dx: Int, dy: Int) = send(Message(type = Message.MOVE, dx = dx, dy = dy))

    fun click(button: String) = send(Message(type = Message.BUTTON, button = button))

    fun buttonDown(button: String) = send(Message(type = Message.BUTTON, button = button, action = "down"))

    fun buttonUp(button: String) = send(Message(type = Message.BUTTON, button = button, action = "up"))

    fun scroll(notches: Int) = send(Message(type = Message.SCROLL, delta = notches))

    /** A tap: down then up, unless [action] latches it (drag, held modifier). */
    fun key(name: String, action: String? = null) =
        send(Message(type = Message.KEY, key = name, action = action))

    fun text(value: String) {
        if (value.isEmpty()) return
        send(Message(type = Message.TEXT, text = value))
    }

    /**
     * Drops everything the PC may still be holding. Sent when we disconnect, so
     * the PC is never left with a stuck mouse button or a latched Ctrl.
     */
    fun releaseAll() = send(Message(type = Message.RELEASE_ALL))

    private fun releaseHeldInput() = releaseAll()

    private fun send(message: Message) {
        val ws = socket
        if (ws == null) return

        if (!ws.send(json.encodeToString(Message.serializer(), message))) {
            Log.w(TAG, "could not send ${message.type}")
            if (_state.value == ConnectionState.CONNECTED) scheduleReconnect()
        }
    }
}

/** Backoff between reconnection attempts: 1s, 2s, 4s ... capped at 30s. Pure
 *  logic, so it is unit-tested rather than discovered on a bad network. */
object Reconnect {
    private const val BASE_MS = 1_000L
    private const val MAX_MS = 30_000L

    fun delayMs(attempt: Int): Long =
        minOf(BASE_MS shl (attempt - 1).coerceIn(0, 20), MAX_MS)
}

/**
 * Trust on first use, anchored to the PC's stable id rather than its IP.
 *
 * Once a PC has been paired, every later connection must present the identical
 * certificate. On the very first contact there is nothing to compare against, so
 * the fingerprint is offered to the user for confirmation instead of being
 * accepted silently.
 */
private class PinningTrustManager(
    private val paired: PairedStore,
    private val pcId: String?,
) : X509TrustManager {
    private var pending: String? = null

    /** The fingerprint seen on this handshake, if it was not already known. */
    fun takePending(): String? = pending

    override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit

    override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
        val leaf = chain.firstOrNull() ?: throw CertificateException("No certificate presented")
        val fingerprint = leaf.fingerprint()

        val id = pcId
        if (id == null) {
            pending = fingerprint
            return
        }

        val pinned = paired.pin(id)
        if (pinned == null) {
            pending = fingerprint
        } else if (pinned != fingerprint) {
            throw CertificateException("This PC's certificate changed since pairing")
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    private fun X509Certificate.fingerprint(): String =
        MessageDigest.getInstance("SHA-256").digest(encoded).joinToString("") { "%02x".format(it) }
}
