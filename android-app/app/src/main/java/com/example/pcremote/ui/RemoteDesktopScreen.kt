package com.example.pcremote.ui

import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewConfiguration
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.TokenStore
import com.example.pcremote.stream.StreamClient
import com.example.pcremote.stream.VideoState
import kotlin.math.abs

/**
 * The desktop-first control surface: the live Windows desktop, touch = mouse.
 * Touch position maps onto the video's coordinate space and is sent as
 * mouse_move_abs; tap = left click, long-press = right click, two-finger
 * vertical drag = scroll. Video rides its own /stream socket; input stays on the
 * control connection, so video back-pressure can never delay clicks.
 */
@Composable
fun RemoteDesktopScreen(
    connection: RemoteConnection,
    pinStore: PinStore,
    tokenStore: TokenStore,
    modifier: Modifier = Modifier,
) {
    val host = connection.currentHost
    if (host == null) {
        Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("Connect to a PC first")
        }
        return
    }

    val client = remember(host) { StreamClient(pinStore, tokenStore) }
    val state by client.state.collectAsState()
    val context = LocalContext.current

    DisposableEffect(host) {
        client.connect(host, connection.currentPort)
        onDispose { client.stop() }
    }
    DisposableEffect(Unit) {
        onDispose { client.shutdown() }
    }

    Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        val dims = (state as? VideoState.Active)?.let { it.width to it.height }
        val slop = ViewConfiguration.get(context).scaledTouchSlop

        // The gesture layer rides the video box itself: AspectRatio letterboxes the
        // SurfaceView, so touch position × (video/box) IS the desktop coordinate.
        var boxSize by remember { mutableStateOf(IntSize.Zero) }
        AndroidView(
            factory = { ctx ->
                SurfaceView(ctx).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = client.decoder.attach(holder.surface)
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                        override fun surfaceDestroyed(holder: SurfaceHolder) = client.decoder.detach()
                    })
                }
            },
            modifier = Modifier
                .then(if (dims != null) Modifier.aspectRatio(dims.first.toFloat() / dims.second) else Modifier.fillMaxSize())
                .fillMaxWidth()
                .onSizeChanged { boxSize = it }
                .desktopGestures(connection, boxSize, dims, ViewConfiguration.getLongPressTimeout().toLong(), slop.toFloat()),
        )

        when (val s = state) {
            is VideoState.Connecting -> Column(horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator()
                Text("Starting screen stream…", modifier = Modifier.padding(top = 12.dp))
            }
            is VideoState.Failed -> Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text("Screen stream unavailable", style = MaterialTheme.typography.titleMedium)
                Text(s.reason, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 4.dp))
                Button(onClick = { client.connect(host, connection.currentPort) }, modifier = Modifier.padding(top = 12.dp)) {
                    Text("Retry")
                }
            }
            is VideoState.Stopped, is VideoState.Idle -> Text("Stream stopped")
            is VideoState.Active -> Unit // the desktop itself IS the UI
        }
    }
}

/**
 * Touch→mouse for the desktop view. Positions scale from the on-screen video box
 * into the desktop's pixel space; taps and long-presses classify on pointer-up.
 */
private fun Modifier.desktopGestures(
    connection: RemoteConnection,
    boxSize: IntSize,
    videoDims: Pair<Int, Int>?,
    longPressTimeoutMs: Long,
    slopPx: Float,
) = pointerInput(videoDims, longPressTimeoutMs) {
    val mapper = DesktopTouchMapper(longPressTimeoutMs, slopPx)
    val sendAbs: (Float, Float) -> Unit = { vx, vy ->
        val (dx, dy) = mapper.toDesktop(vx, vy, boxSize, videoDims)
        connection.sendMouseAbs(dx, dy)
    }
    awaitEachGesture {
        val down = awaitFirstDown()
        mapper.down(down.position, down.uptimeMillis, sendAbs)
        var scrolling = false
        var scrollAccum = 0f
        while (true) {
            val event = awaitPointerEvent()
            val pressedCount = event.changes.count { it.pressed }
            if (pressedCount == 0) {
                for (action in mapper.up(event.changes.first().uptimeMillis)) action(connection)
                break
            }
            val change = event.changes.first()
            if (pressedCount >= 2) {
                scrolling = true
                scrollAccum += change.positionChange().y
                if (abs(scrollAccum) >= SCROLL_STEP_PX) {
                    val steps = (scrollAccum / SCROLL_STEP_PX).toInt().coerceIn(-5, 5)
                    scrollAccum -= steps * SCROLL_STEP_PX
                    connection.sendScroll(steps)
                }
            } else if (!scrolling) {
                mapper.move(change.position, change.uptimeMillis, sendAbs)
            }
        }
    }
}

/** Gesture state machine: throttle, tap/long-press classification, desktop mapping. */
internal class DesktopTouchMapper(
    private val longPressTimeoutMs: Long,
    private val slopPx: Float,
) {
    private var downMs = 0L
    private var lastSentMs = 0L
    private var lastX = 0f
    private var lastY = 0f
    private var totalMove = 0f
    private var moved = false

    fun down(pos: Offset, now: Long, move: (Float, Float) -> Unit) {
        downMs = now
        lastSentMs = 0L
        lastX = pos.x
        lastY = pos.y
        totalMove = 0f
        moved = false
        sendMove(pos.x, pos.y, now, move, force = true)
    }

    fun move(pos: Offset, now: Long, move: (Float, Float) -> Unit) {
        totalMove += abs(pos.x - lastX) + abs(pos.y - lastY)
        if (totalMove > slopPx) moved = true
        lastX = pos.x
        lastY = pos.y
        sendMove(pos.x, pos.y, now, move, force = false)
    }

    /** Click actions to run on pointer-up. */
    fun up(now: Long): List<(RemoteConnection) -> Unit> {
        val elapsed = now - downMs
        return when {
            elapsed < longPressTimeoutMs && !moved -> listOf({ c -> c.sendMouseClick("left", "click") })
            elapsed >= longPressTimeoutMs && !moved -> listOf({ c -> c.sendMouseClick("right", "click") })
            else -> emptyList() // drag: absolute moves were already sent
        }
    }

    /** Scales a view position into the desktop's pixel space (aspect-fit safe). */
    fun toDesktop(x: Float, y: Float, boxSize: IntSize, videoDims: Pair<Int, Int>?): Pair<Int, Int> {
        val (vw, vh) = videoDims ?: return 0 to 0
        if (boxSize.width <= 0 || boxSize.height <= 0) return 0 to 0
        val dx = x / boxSize.width * vw
        val dy = y / boxSize.height * vh
        return dx.toInt().coerceIn(0, vw - 1) to dy.toInt().coerceIn(0, vh - 1)
    }

    private fun sendMove(x: Float, y: Float, now: Long, move: (Float, Float) -> Unit, force: Boolean) {
        if (!force && now - lastSentMs < MOVE_THROTTLE_MS) return
        lastSentMs = now
        move(x, y)
    }

    private companion object {
        const val MOVE_THROTTLE_MS = 30L
    }
}

private const val SCROLL_STEP_PX = 48f
