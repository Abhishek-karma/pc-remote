package com.example.pcremote.network

/**
 * Reconnect timing policy (Phase E hardening): exponential backoff per
 * attempt, capped at [maxDelayMs], and a cumulative time ceiling after which
 * auto-reconnect gives up and hands control back to the user (the banner's
 * Retry action calls [RemoteConnection.reconnectLast]). Pure logic, unit-tested.
 *
 * @param maxDurationMs total time auto-reconnect may keep trying (default 5 min —
 *   long enough to ride out an agent restart, short enough that a dead PC
 *   doesn't leave a forever-silent retry loop).
 */
class ReconnectPolicy(
    private val baseDelayMs: Long = 1_000,
    private val maxDelayMs: Long = 30_000,
    val maxDurationMs: Long = 5 * 60_000
) {
    /** Backoff for the Nth attempt (1-based): 1s, 2s, 4s … capped at [maxDelayMs]. */
    fun delayMs(attempt: Int): Long =
        minOf(baseDelayMs shl (attempt - 1).coerceIn(0, 30), maxDelayMs)

    /** True when auto-reconnect should stop and surface a manual Retry. */
    fun shouldGiveUp(elapsedMs: Long): Boolean = elapsedMs >= maxDurationMs
}
