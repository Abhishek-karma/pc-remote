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
 * Host/port validation rules shared by the connection layer and the pairing UI,
 * so "what the UI accepts" and "what OkHttp can build a URL for" never drift.
 */
object ConnectionEndpoint {
    const val DEFAULT_PORT = 58642

    /** Hostnames (RFC 1123 labels) and IPv4 literals only — no bare IPv6. */
    private val HostPattern = Regex("^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$")

    fun hostIsValid(host: String): Boolean {
        val trimmed = host.trim()
        return trimmed.isNotEmpty() && trimmed.length <= 253 && HostPattern.matches(trimmed)
    }

    fun portIsValid(port: Int) = port in 1..65535

    fun isConnectable(host: String, port: Int) = hostIsValid(host) && portIsValid(port)
}

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
    val errorCode: String? = null,
    val connKey: String? = null
)

/**
 * Persists a per-host SHA-256 fingerprint of the agent's self-signed server
 * certificate, captured on the first successful handshake (trust-on-first-use,
 * docs/09-SECURITY-PRIVACY.md §2). Later connections to the same host must
 * present the same certificate — a mismatch (e.g. a different agent after a
 * DHCP renumber) fails the TLS handshake instead of silently trusting it.
 *
 * Pins are stored under both IP and pcName keys so DHCP lease changes
 * don't force re-pairing when the machine name is known.
 */
class PinStore(private val prefs: SharedPreferences) {
    fun getPin(host: String): String? =
        prefs.getString("certpin_$host", null)

    /** Look up pin by pcName fallback when IP-based pin is missing. */
    fun getPinByName(pcName: String): String? =
        prefs.getString("certpin_name_$pcName", null)

    /** Records the first-seen pin only; later connections can never rewrite it. */
    fun recordPin(host: String, fingerprint: String, pcName: String? = null) {
        val edit = prefs.edit()
        if (prefs.getString("certpin_$host", null) == null) {
            edit.putString("certpin_$host", fingerprint)
        }
        if (pcName != null && prefs.getString("certpin_name_$pcName", null) == null) {
            edit.putString("certpin_name_$pcName", fingerprint)
        }
        edit.apply()
    }

    /**
     * Removes the pin so the host can be re-paired trust-on-first-use (e.g.
     * after the agent was reinstalled and generated a new certificate).
     * Only ever called from explicit user action ("Forget").
     */
    fun clearPin(host: String) {
        prefs.edit().remove("certpin_$host").apply()
    }

    /**
     * Migrate an IP-based pin entry to cover a new IP (DHCP change). Only records
     * the alias when it matches the existing pin for the old host — otherwise a
     * spoofed or mismatched fingerprint could be persisted for the new IP.
     */
    fun aliasPin(newHost: String, fingerprint: String, oldHost: String? = null) {
        if (prefs.getString("certpin_$newHost", null) != null) return
        if (oldHost != null) {
            val existing = prefs.getString("certpin_$oldHost", null)
            if (existing != null && existing != fingerprint) return
        }
        prefs.edit().putString("certpin_$newHost", fingerprint).apply()
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
    // Server-assigned connection key returned in auth_ok; sent on disconnect so the
    // server can release the slot immediately without waiting for TCP timeout.
    private var currentConnKey: String? = null

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

    companion object {
        var activeInstance: RemoteConnection? = null
    }

    /** UI reads this to show "PC is shutting down…" instead of a retry banner. */
    val lastDisconnectExpected: Boolean get() = expectedDisconnect

    /** Drops any in-flight reconnect and starts a fresh connection. */
    fun connect(host: String, port: Int = 58642, pairingCode: String? = null) {
        activeInstance = this
        reconnectJob?.cancel()
        reconnectAttemptInternal = 0
        intentionallyClosed = false
        authFailed = false
        expectedDisconnect = false

        // OkHttp throws IllegalArgumentException synchronously from
        // Request.Builder().url() for a host it cannot parse, which would
        // escape the click handler and crash the app. Validate first and
        // surface a normal FAILED state instead.
        val target = host.trim()
        if (!ConnectionEndpoint.isConnectable(target, port)) {
            currentHostInternal = target.ifEmpty { null }
            currentPortInternal = port
            _state.value = ConnectionState.FAILED
            return
        }

        currentHostInternal = target
        currentPortInternal = port
        _state.value = ConnectionState.CONNECTING
        doConnect(target, port, pairingCode = pairingCode)
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
        // Send disconnect message with our connKey so the server can release the slot immediately.
        currentConnKey?.let { connKey ->
            sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "disconnect", connKey = connKey))
        }
        currentConnKey = null
        webSocket?.close(1000, "user disconnected")
        webSocket = null
        _state.value = ConnectionState.DISCONNECTED
    }

    fun shutdown() {
        disconnect()
        scope.cancel()
        // Drop the static reference: the foreground service and any later
        // onStartCommand must not hold a cancelled connection graph alive.
        if (activeInstance === this) activeInstance = null
    }

    private fun doConnect(host: String, port: Int, pairingCode: String?) {
        // Defence in depth: OkHttp throws IllegalArgumentException from
        // url() for hosts it cannot parse, which must never escape to the
        // caller's click handler as a crash.
        val request = try
        {
            Request.Builder().url("wss://$host:$port/").build()
        }
        catch (e: IllegalArgumentException)
        {
            android.util.Log.w("RemoteConnection", "Rejected unsupported host '$host': ${e.message}")
            webSocket = null
            _state.value = ConnectionState.FAILED
            return
        }
        webSocket = clientFor(host).newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                val savedToken = tokenStore.getToken(host)
                sendRaw(RemoteMessage(version = 1, requestId = generateRequestId(), type = "auth", token = savedToken, pairingCode = pairingCode))
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val msg = runCatching { json.decodeFromString<RemoteMessage>(text) }.getOrNull() ?: return
                when (msg.type) {
                    "auth_ok" -> {
                        val pcName = msg.pcName
                        activeTrustManagers[host]?.pendingFingerprint?.let { fingerprint ->
                            pinStore.recordPin(host, fingerprint, pcName)
                        }
                        activeTrustManagers[host]?.clearPending()
                        msg.token?.let { tokenStore.saveToken(host, it, pcName) }
                        pcName?.let { onPcName?.invoke(host, it) }
                        currentConnKey = msg.connKey
                        authFailed = false
                        if (_state.value == ConnectionState.RECONNECTING) reconnectAttemptInternal = 0
                        _state.value = ConnectionState.CONNECTED
                    }
                    "auth_failed" -> {
                        activeTrustManagers[host]?.clearPending()
                        // A rejected pairing code or a revoked token cannot get
                        // better by retrying with the same secret: give the
                        // user an error they can act on, not a silent backoff
                        // loop that also feeds the agent's IP lockout.
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
                    // Do NOT auto-clear pin on failure (security risk: MITM certificate poisoning vector).
                    // Pins are cleared only via explicit user action.
                    _state.value = ConnectionState.FAILED
                } else if (authFailed) {
                    // Keep the auth failure visible: this callback fires right
                    // after auth_failed and must not overwrite it.
                    _state.value = ConnectionState.FAILED
                } else if (tokenStore.getToken(host) != null) {
                    scheduleReconnect(host, port)
                } else {
                    _state.value = ConnectionState.FAILED
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (intentionallyClosed || expectedDisconnect) {
                    _state.value = ConnectionState.DISCONNECTED
                } else if (authFailed) {
                    _state.value = ConnectionState.FAILED
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
            // Only one PC connects at a time; evict stale entries instead of
            // letting the map grow, and actually release their sockets.
            if (hostClients.size >= 3) {
                val stale = hostClients.keys.first()
                hostClients.remove(stale)?.let { old ->
                    runCatching {
                        old.dispatcher.cancelAll()
                        old.connectionPool.evictAll()
                    }
                }
                activeTrustManagers.remove(stale)
            }
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
        val sent = webSocket?.send(text) ?: false
        if (!sent && _state.value == ConnectionState.CONNECTED) {
            android.util.Log.w("RemoteConnection", "WebSocket send failed for type=${message.type}")
            currentHostInternal?.let { h -> scheduleReconnect(h, currentPortInternal) }
        }
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

    /** Look up token by pcName fallback when IP-based token is missing. */
    fun getTokenByName(pcName: String): String? = prefs.getString("token_name_$pcName", null)

    fun saveToken(host: String, token: String, pcName: String? = null) {
        val edit = prefs.edit()
        edit.putString("token_$host", token)
        if (pcName != null) edit.putString("token_name_$pcName", token)
        // Maintain an index so allHosts() is O(1) instead of scanning every pref key.
        val count = prefs.getInt("_host_count", 0)
        edit.putString("token_host_$count", host)
        edit.putInt("_host_count", count + 1)
        edit.apply()
    }

    /** Removes a saved token locally ("Forget this PC" in Settings). */
    fun forget(host: String) = prefs.edit().remove("token_$host").apply()

    /** Hosts with a saved token, sorted — feeds the Settings "Paired PCs" list. */
    fun allHosts(): List<String> =
        (0 until prefs.getInt("_host_count", 0))
            .mapNotNull { prefs.getString("token_host_$it", null) }
            .sorted()
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

/** Creates the app's encrypted SharedPreferences with automatic KeyStore recovery (09-SECURITY-PRIVACY.md §4). */
object EncryptedPrefs {
    fun create(context: android.content.Context): android.content.SharedPreferences {
        return try {
            val masterKey = androidx.security.crypto.MasterKey.Builder(context)
                .setKeyScheme(androidx.security.crypto.MasterKey.KeyScheme.AES256_GCM)
                .build()
            androidx.security.crypto.EncryptedSharedPreferences.create(
                context,
                "pc_remote_prefs",
                masterKey,
                androidx.security.crypto.EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                androidx.security.crypto.EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
            )
        } catch (e: Exception) {
            try {
                context.deleteSharedPreferences("pc_remote_prefs")
                val masterKey = androidx.security.crypto.MasterKey.Builder(context)
                    .setKeyScheme(androidx.security.crypto.MasterKey.KeyScheme.AES256_GCM)
                    .build()
                androidx.security.crypto.EncryptedSharedPreferences.create(
                    context,
                    "pc_remote_prefs",
                    masterKey,
                    androidx.security.crypto.EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                    androidx.security.crypto.EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
                )
            } catch (e: Exception) {
                // Never fall back to plaintext prefs: tokens and cert pins would be
                // written to disk unencrypted. Use an in-memory store instead so the
                // app still works for this session without persisting secrets.
                android.util.Log.e("EncryptedPrefs", "Encrypted prefs unavailable; using in-memory store (no persistence)", e)
                InMemorySharedPreferences()
            }
        }
    }
}

/**
 * Non-persisting [SharedPreferences] used only when [EncryptedSharedPreferences]
 * is unavailable, so auth tokens and certificate pins are never written to disk
 * in plaintext. Values live for the process lifetime only.
 */
private class InMemorySharedPreferences : android.content.SharedPreferences {
    private val map = mutableMapOf<String, Any?>()
    private val listeners = mutableListOf<android.content.SharedPreferences.OnSharedPreferenceChangeListener>()

    override fun getAll(): Map<String, Any?> = HashMap(map)
    override fun getString(key: String, defValue: String?): String? = map[key] as? String ?: defValue
    override fun getStringSet(key: String, defValue: Set<String>?): Set<String>? = map[key] as? Set<String> ?: defValue
    override fun getInt(key: String, defValue: Int): Int = map[key] as? Int ?: defValue
    override fun getLong(key: String, defValue: Long): Long = map[key] as? Long ?: defValue
    override fun getFloat(key: String, defValue: Float): Float = map[key] as? Float ?: defValue
    override fun getBoolean(key: String, defValue: Boolean): Boolean = map[key] as? Boolean ?: defValue
    override fun contains(key: String): Boolean = map.containsKey(key)
    override fun edit(): android.content.SharedPreferences.Editor = Editor()

    override fun registerOnSharedPreferenceChangeListener(listener: android.content.SharedPreferences.OnSharedPreferenceChangeListener) {
        listeners.add(listener)
    }

    override fun unregisterOnSharedPreferenceChangeListener(listener: android.content.SharedPreferences.OnSharedPreferenceChangeListener) {
        listeners.remove(listener)
    }

    private fun notifyChanged(key: String) {
        listeners.forEach { it.onSharedPreferenceChanged(this, key) }
    }

    inner class Editor : android.content.SharedPreferences.Editor {
        private val pending = mutableMapOf<String, Any?>()
        private val removals = mutableSetOf<String>()
        private var clearAll = false

        override fun putString(key: String, value: String?): android.content.SharedPreferences.Editor {
            if (value == null) removals.add(key) else pending[key] = value
            return this
        }

        override fun putStringSet(key: String, value: Set<String>?): android.content.SharedPreferences.Editor {
            if (value == null) removals.add(key) else pending[key] = value
            return this
        }

        override fun putInt(key: String, value: Int): android.content.SharedPreferences.Editor { pending[key] = value; return this }
        override fun putLong(key: String, value: Long): android.content.SharedPreferences.Editor { pending[key] = value; return this }
        override fun putFloat(key: String, value: Float): android.content.SharedPreferences.Editor { pending[key] = value; return this }
        override fun putBoolean(key: String, value: Boolean): android.content.SharedPreferences.Editor { pending[key] = value; return this }
        override fun remove(key: String): android.content.SharedPreferences.Editor { removals.add(key); return this }
        override fun clear(): android.content.SharedPreferences.Editor { clearAll = true; return this }
        override fun commit(): Boolean { apply(); return true }

        override fun apply() {
            if (clearAll) map.clear()
            removals.forEach { map.remove(it) }
            map.putAll(pending)
            pending.keys.forEach { notifyChanged(it) }
            removals.forEach { notifyChanged(it) }
            pending.clear(); removals.clear(); clearAll = false
        }
    }
}