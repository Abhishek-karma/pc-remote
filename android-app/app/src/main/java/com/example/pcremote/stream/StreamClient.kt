package com.example.pcremote.stream

import com.example.pcremote.network.PinStore
import com.example.pcremote.network.PinningTrustManager
import com.example.pcremote.network.RemoteMessage
import com.example.pcremote.network.TokenStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext

/** Live state of the video channel, surfaced to the remote-desktop UI. */
sealed interface VideoState {
    data object Idle : VideoState
    data object Connecting : VideoState
    data class Active(val width: Int, val height: Int, val fps: Int) : VideoState
    data class Failed(val reason: String) : VideoState
    data object Stopped : VideoState
}

/**
 * The video half of a session: a SECOND WebSocket on /stream (same TLS listener,
 * same TOFU pin — pairing happens on the control connection first). Carries
 * authenticated JSON lifecycle control in one direction and binary MediaFrames in
 * the other, so video can never back-pressure input (which rides the control
 * socket).
 *
 * Byte flow: WebSocket binary → [MediaFrame.parse] → [FragmentedMp4Demuxer] →
 * [H264StreamDecoder] → Surface.
 */
class StreamClient(
    private val pinStore: PinStore,
    private val tokenStore: TokenStore,
) {
    private val json = Json { ignoreUnknownKeys = true }
    private val demuxer = FragmentedMp4Demuxer(DemuxerSink())
    val decoder = H264StreamDecoder(onDecoderBehind = { requestKeyframe() })

    private val _state = MutableStateFlow<VideoState>(VideoState.Idle)
    val state: StateFlow<VideoState> = _state

    private var webSocket: WebSocket? = null
    private var host: String? = null
    private var port: Int = DEFAULT_STREAM_PORT

    fun connect(targetHost: String, targetPort: Int = DEFAULT_STREAM_PORT) {
        host = targetHost
        port = targetPort
        val savedPin = pinStore.getPin(targetHost)
        if (savedPin == null) {
            // Never stream to an unpinned host: pairing happens on the control socket.
            _state.value = VideoState.Failed("not paired")
            return
        }
        _state.value = VideoState.Connecting

        val trustManager = PinningTrustManager(pinStore, targetHost)
        val ssl = SSLContext.getInstance("TLS")
        ssl.init(null, arrayOf(trustManager), SecureRandom())
        val client = OkHttpClient.Builder()
            .sslSocketFactory(ssl.socketFactory, trustManager)
            .hostnameVerifier { _, _ -> true } // pin is by fingerprint
            .pingInterval(15, TimeUnit.SECONDS)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()

        val token = tokenStore.getToken(targetHost)
        val request = Request.Builder().url("wss://$targetHost:$targetPort/stream").build()
        webSocket = client.newWebSocket(request, Listener(token))
    }

    /** Stops the stream and closes the media socket. */
    fun stop() {
        send(RemoteMessage(version = 1, requestId = requestId(), type = "stream_stop"))
        webSocket?.close(1000, "stream stopped")
        webSocket = null
        _state.value = VideoState.Stopped
        // Both parsers must forget the finished stream: reusing them for the next one
        // would keep stale SPS/PPS and decoder state (different resolution).
        demuxer.reset()
        decoder.reset()
    }

    fun shutdown() {
        webSocket?.cancel()
        webSocket = null
        decoder.release()
    }

    private fun requestKeyframe() {
        send(RemoteMessage(version = 1, requestId = requestId(), type = "keyframe_request"))
    }

    private fun send(message: RemoteMessage) {
        webSocket?.send(json.encodeToString(RemoteMessage.serializer(), message))
    }

    private fun requestId(): String =
        java.util.UUID.randomUUID().toString().substring(0, 8)

    private inner class Listener(private val token: String?) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            send(RemoteMessage(version = 1, requestId = requestId(), type = "auth", token = token))
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            val msg = runCatching { json.decodeFromString(RemoteMessage.serializer(), text) }.getOrNull() ?: return
            when (msg.type) {
                "auth_ok" -> send(RemoteMessage(version = 1, requestId = requestId(), type = "stream_start"))
                "stream_state" -> {
                    _state.value = when (msg.streamState) {
                        "active" -> VideoState.Active(msg.width ?: 0, msg.height ?: 0, msg.fps ?: 0)
                        "stopped" -> VideoState.Stopped
                        else -> VideoState.Failed(msg.errorCode ?: "stream error")
                    }
                }
            }
        }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            val frame = MediaFrame.parse(bytes.toByteArray()) ?: return
            demuxer.feed(frame.payload)
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            _state.value = VideoState.Failed(t.message ?: "stream connection failed")
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (_state.value !is VideoState.Stopped) _state.value = VideoState.Stopped
        }
    }

    private inner class DemuxerSink : FragmentedMp4Demuxer.Listener {
        override fun onInit(csd0: ByteArray, csd1: ByteArray, width: Int, height: Int) {
            decoder.onInit(csd0, csd1, width, height)
        }

        override fun onSample(data: ByteArray, ptsUs: Long, isSync: Boolean) {
            decoder.onSample(data, ptsUs, isSync)
        }
    }

    private companion object {
        /** Same TLS listener as the control channel (no extra firewall port). */
        const val DEFAULT_STREAM_PORT = 58642
    }
}
