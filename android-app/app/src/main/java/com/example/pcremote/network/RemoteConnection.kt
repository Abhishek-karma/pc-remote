package com.example.pcremote.network

import android.content.SharedPreferences
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

enum class ConnectionState { DISCONNECTED, CONNECTING, AWAITING_PAIRING, CONNECTED, RECONNECTING, FAILED }

/**
 * Message shape shared with the Windows agent's RemoteMessage class.
 * Keep field names identical (camelCase matches the C# JsonPropertyName values).
 */
@Serializable
data class RemoteMessage(
    val version: Int = 1,
    val requestId: String? = null,
    val type: String,
    val reason: String? = null,
    val dx: Int? = null,
    val dy: Int? = null,
    val button: String? = null,
    val action: String? = null,
    val key: String? = null,
    val modifiers: List<String>? = null,
    val text: String? = null,
    val token: String? = null,
    val pairingCode: String? = null,
    val pcName: String? = null,
    val success: Boolean? = null,
    val errorCode: String? = null
)

/**
 * Persists a per-host SHA-256 fingerprint of the agent's self-signed server
 * certificate, captured on the first successful handshake (trust-on-first-use,
 * docs/09-SECURITY-PRIVACY.md §2). Later connections to the same host must
 * present the same certificate — a mismatch (e.g. a different agent after a
 * DHCP renumber) fails the TLS handshake instead of silently trusting it.
 */
class PinStore(private val prefs: SharedPreferences) {
    fun getPin(host: String): String? = prefs.getString("certpin_$host", null)

    /** Records the first-seen pin only; later connections can never rewrite it. */
    fun recordPin(host: String, fingerprint: String) {
        if (prefs.getString("certpin_$host", null) == null) {
            prefs.edit().putString("certpin_$host", fingerprint).apply()
        }
    }

    /**
     * Removes the pin so the host can be re-paired trust-on-first-use (e.g.
     * after the agent was reinstalled and generated a new certificate).
     * Only ever called from explicit user action ("Forget").
     */
    fun clearPin(host: String) {
        prefs.edit().remove("certpin_$host").apply()
    }
}

/**
 * Wraps a single WebSocket-over-TLS connection to the PC agent. Call connect()
 * once you have an IP (from discovery or manual entry) and a pairing code
 * (first-time pairing) or a saved token (returning device).
 *
 * Security: WSS only, with TOFU server-certificate pinning per host. When a
 * previously-connected host drops, the connection re-authenticates itself
 * with the saved token using exponential backoff (RECONNECTING), so a Wi-Fi
 * hiccup heals without user action (docs/10-ERROR-HANDLING.md §5).
 */
class RemoteConnection(
    private val tokenStore: TokenStore,
    private val pinStore: PinStore,
    /** Called when the agent reports its machine name (auth_ok) — lets the
     *  app show the real PC name even for manual-IP pairings. */
    private val onPcName: ((host: String, name: String) -> Unit)? = null
) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val baseClient = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .connectTimeout(10, TimeUnit.SECONDS)
        // No read timeout: a long-lived WebSocket should only die on ping failure.
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    private val json = Json { ignoreUnknownKeys = true }

    private val hostClients = mutableMapOf<String, OkHttpClient>()
    private val activeTrustManagers = mutableMapOf<String, PinningTrustManager>()
    private var webSocket: WebSocket? = null
    private var currentHostInternal: String? = null
    private var currentPortInternal = 58642
    private var reconnectJob: Job? = null
    private var reconnectAttemptInternal = 0
    private var reconnectStartedAt = 0L
    private val reconnectPolicy = ReconnectPolicy()
    private var intentionallyClosed = false

        private val _state = MutableStateFlow(ConnectionState.DISCONNECTED)
    val state: StateFlow<ConnectionState> = _state

    /** Host/port of the active or last connection attempt (details sheet). */
    val currentHost: String? get() = currentHostInternal
    val currentPort: Int get() = currentPortInternal

    /** Current reconnect attempt number (1-based; 0 when not reconnecting). */
    val reconnectAttempt: Int get() = reconnectAttemptInternal

    // True after auth_failed: the saved token was rejected, so auto-reconnect
    // would just fail in a loop until the user re-pairs.
    private var authFailed = false

    /** UI reads this to send the user back to pairing instead of retrying. */
    val lastAuthFailed: Boolean get() = authFailed

    // True after the agent announced an expected close (user-initiated
    // shutdown/restart — 10-ERROR-HANDLING.md §3): suppresses the reconnect
    // banner and any auto-reconnect attempts.
    private var expectedDisconnect = false

    /** UI reads this to show "PC is shutting down…" instead of a retry banner. */
    val lastDisconnectExpected: Boolean get() = expectedDisconnect

    /** Drops any in-flight reconnect and starts a fresh connection. */
    fun connect(host: String, port: Int = 58642, pairingCode: String? = null) {
        reconnectJob?.cancel()
        reconnectAttemptInternal = 0
        intentionallyClosed = false
        authFailed = false
        expectedDisconnect = false
        currentHostInternal = host
        currentPortInternal = port
        _state.value = ConnectionState.CONNECTING
        doConnect(host, port, pairingCode = pairingCode)
    }

    /** Reconnects to the last host using only its saved token (no code prompt). */
    fun reconnectLast() {
        val host = currentHostInternal ?: return
        connect(host, currentPort)
    }

    /**
     * Manual close: cancels reconnects and marks the close as intentional so
     * the failure/closed callbacks do not re-enter the reconnect loop.
     */
    fun disconnect() {
        intentionallyClosed = true
        reconnectJob?.cancel()
        reconnectJob = null
        webSocket?.close(1000, "user disconnected")
        webSocket = null
        _state.value = ConnectionState.DISCONNECTED
    }

    fun shutdown() {
        disconnect()
        scope.cancel()
    }

    private fun doConnect(host: String, port: Int, pairingCode: String?) {
        val request = Request.Builder().url("wss://$host:$port/").build()
        webSocket = clientFor(host).newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                val savedToken = tokenStore.getToken(host)
                sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "auth", token = savedToken, pairingCode = pairingCode))
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val msg = runCatching { json.decodeFromString<RemoteMessage>(text) }.getOrNull() ?: return
                when (msg.type) {
                    "auth_ok" -> {
                        activeTrustManagers[host]?.pendingFingerprint?.let { fingerprint ->
                            pinStore.recordPin(host, fingerprint)
                        }
                        activeTrustManagers[host]?.clearPending()
                        msg.token?.let { tokenStore.saveToken(host, it) }
                        msg.pcName?.let { onPcName?.invoke(host, it) }
                        authFailed = false
                        if (_state.value == ConnectionState.RECONNECTING) reconnectAttemptInternal = 0
                        _state.value = ConnectionState.CONNECTED
                    }
                    "auth_failed" -> {
                        activeTrustManagers[host]?.clearPending()
                        authFailed = true
                        _state.value = ConnectionState.FAILED
                    }
                    "disconnecting" -> expectedDisconnect = true
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                activeTrustManagers[host]?.clearPending()
                if (intentionallyClosed || expectedDisconnect) {
                    _state.value = ConnectionState.DISCONNECTED
                } else if (t.isCertificateProblem()) {
                    // A changed server certificate for a pinned host cannot be
                    // fixed by retrying — surface the failure for re-pairing.
                    _state.value = ConnectionState.FAILED
                } else if (!authFailed && tokenStore.getToken(host) != null) {
                    scheduleReconnect(host, port)
                } else {
                    _state.value = ConnectionState.FAILED
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (intentionallyClosed || authFailed || expectedDisconnect) {
                    _state.value = ConnectionState.DISCONNECTED
                } else if (tokenStore.getToken(host) != null) {
                    scheduleReconnect(host, port)
                } else {
                    _state.value = ConnectionState.DISCONNECTED
                }
            }
        })
    }

    private fun scheduleReconnect(host: String, port: Int) {
        if (intentionallyClosed || authFailed) return
        val attempt = reconnectAttemptInternal + 1
        // Cumulative ceiling: stop silently retrying after ~5 minutes and let
        // the user decide (Phase E) — the banner's Retry restarts cleanly.
        if (attempt == 1) reconnectStartedAt = android.os.SystemClock.elapsedRealtime()
        val elapsed = android.os.SystemClock.elapsedRealtime() - reconnectStartedAt
        if (reconnectPolicy.shouldGiveUp(elapsed)) {
            _state.value = ConnectionState.FAILED
            return
        }
        reconnectAttemptInternal = attempt
        _state.value = ConnectionState.RECONNECTING
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            delay(reconnectPolicy.delayMs(attempt))
            if (!intentionallyClosed && !authFailed) doConnect(host, port, pairingCode = null)
        }
    }

    /** True when some exception in the chain is a certificate-validation problem. */
    private fun Throwable.isCertificateProblem(): Boolean {
        var current: Throwable? = this
        while (current != null) {
            if (current is CertificateException ||
                current.message?.contains("Certificate", ignoreCase = true) == true
            ) return true
            current = current.cause
        }
        return false
    }

    private fun clientFor(host: String): OkHttpClient =
        hostClients.getOrPut(host) {
            val trustManager = PinningTrustManager(pinStore, host)
            activeTrustManagers[host] = trustManager
            val sslContext = SSLContext.getInstance("TLS")
            sslContext.init(null, arrayOf<X509TrustManager>(trustManager), SecureRandom())
            baseClient.newBuilder()
                .sslSocketFactory(sslContext.socketFactory, trustManager)
                // Pinning is by fingerprint; certificate-hostname checks are redundant here.
                .hostnameVerifier { _, _ -> true }
                .build()
        }

    // --- Command helpers used directly by the UI layer ---

    private fun generateRequestId(): String =
        java.util.UUID.randomUUID().toString().substring(0, 8)

    fun sendMouseMove(dx: Int, dy: Int) =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "mouse_move", dx = dx, dy = dy))

    fun sendMouseClick(button: String = "left", action: String = "click") =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "mouse_click", button = button, action = action))

    fun sendScroll(dy: Int) =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "mouse_scroll", dy = dy))

    fun sendKey(key: String, modifiers: List<String> = emptyList()) =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "key_press", key = key, modifiers = modifiers))

    fun sendText(text: String) =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "text_input", text = text))

    fun sendMedia(action: String) =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "media_control", action = action))

    fun sendPower(action: String) =
        sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "system_power", action = action))

    private fun sendRaw(message: RemoteMessage) {
        val text = json.encodeToString(message)
        webSocket?.send(text)
    }
}

/**
 * Trust-on-first-use pinning: the first certificate seen for a host is
 * recorded ONLY AFTER successful authentication (auth_ok).
 * Every later handshake must present the identical certificate.
 */
internal class PinningTrustManager(private val pinStore: PinStore, private val host: String) : X509TrustManager {

    var pendingFingerprint: String? = null
        private set

    fun clearPending() {
        pendingFingerprint = null
    }

    override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) {}

    override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
        val leaf = chain.firstOrNull() ?: throw CertificateException("No server certificate presented")
        val fingerprint = leaf.sha256Fingerprint()
        val pinned = pinStore.getPin(host)
        if (pinned != null && pinned != fingerprint) {
            throw CertificateException("Certificate changed for $host (pinned $pinned, saw $fingerprint)")
        }
        if (pinned == null) {
            pendingFingerprint = fingerprint
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
}

private fun X509Certificate.sha256Fingerprint(): String =
    MessageDigest.getInstance("SHA-256").digest(encoded).joinToString("") { "%02x".format(it) }

/**
 * Persists per-host trust tokens so returning to a previously-paired PC
 * doesn't require re-entering the pairing code. Backed by
 * EncryptedSharedPreferences (09-SECURITY-PRIVACY.md §4).
 */
class TokenStore(private val prefs: android.content.SharedPreferences) {
    fun getToken(host: String): String? = prefs.getString("token_$host", null)
    fun saveToken(host: String, token: String) {
        prefs.edit().putString("token_$host", token).apply()
    }

    /** Removes a saved token locally ("Forget this PC" in Settings). */
    fun forget(host: String) = prefs.edit().remove("token_$host").apply()

    /** Hosts with a saved token, sorted — feeds the Settings "Paired PCs" list. */
    fun allHosts(): List<String> {
        val prefix = "token_"
        return prefs.all.keys.filter { it.startsWith(prefix) }
            .map { it.removePrefix(prefix) }
            .sorted()
    }
}

/**
 * App settings persisted in SharedPreferences: touchpad sensitivity
 * (02-FEATURE-SPECIFICATION.md F2.6/F9.2) and optional per-PC display names
 * (F9.1). Exposed as flows so screens pick changes up live.
 */
class SettingsStore(private val prefs: android.content.SharedPreferences) {
    companion object {
        const val SENSITIVITY_MIN = 0.5f
        const val SENSITIVITY_MAX = 3.0f
        const val SENSITIVITY_DEFAULT = 1.5f
        private const val KEY_SENSITIVITY = "touchpad_sensitivity"
        private const val KEY_HAPTICS = "haptics_enabled"
        private const val KEY_TOUCHPAD_HINT_SEEN = "touchpad_hint_seen"
        private const val NAME_PREFIX = "pc_name_"
    }

    private val _sensitivity = MutableStateFlow(prefs.getFloat(KEY_SENSITIVITY, SENSITIVITY_DEFAULT))
    val sensitivity: StateFlow<Float> = _sensitivity

    private val _hapticsEnabled = MutableStateFlow(prefs.getBoolean(KEY_HAPTICS, true))
    val hapticsEnabled: StateFlow<Boolean> = _hapticsEnabled

    private val _touchpadHintSeen = MutableStateFlow(prefs.getBoolean(KEY_TOUCHPAD_HINT_SEEN, false))
    val touchpadHintSeen: StateFlow<Boolean> = _touchpadHintSeen

    fun setSensitivity(value: Float) {
        _sensitivity.value = value
        prefs.edit().putFloat(KEY_SENSITIVITY, value).apply()
    }

    fun setHapticsEnabled(enabled: Boolean) {
        _hapticsEnabled.value = enabled
        prefs.edit().putBoolean(KEY_HAPTICS, enabled).apply()
    }

    fun markTouchpadHintSeen() {
        _touchpadHintSeen.value = true
        prefs.edit().putBoolean(KEY_TOUCHPAD_HINT_SEEN, true).apply()
    }

    /** Display name for a host (empty means "show the IP"). */
    fun getName(host: String): String? = prefs.getString(NAME_PREFIX + host, null)

    fun setName(host: String, name: String) =
        prefs.edit().putString(NAME_PREFIX + host, name.trim()).apply()

    fun removeName(host: String) = prefs.edit().remove(NAME_PREFIX + host).apply()
}

/** Creates the app's encrypted SharedPreferences (09-SECURITY-PRIVACY.md §4). */
object EncryptedPrefs {
    fun create(context: android.content.Context): android.content.SharedPreferences {
        val masterKey = androidx.security.crypto.MasterKey.Builder(context)
            .setKeyScheme(androidx.security.crypto.MasterKey.KeyScheme.AES256_GCM)
            .build()
        return androidx.security.crypto.EncryptedSharedPreferences.create(
            context,
            "pc_remote_prefs",
            masterKey,
            androidx.security.crypto.EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            androidx.security.crypto.EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }
}