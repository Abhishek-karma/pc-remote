package com.example.pcremote.network

import android.content.Context
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Live E2E against a real agent on the LAN (11-TESTING-STRATEGY.md §5).
 * Not part of the normal connected suite — run it explicitly with the agent's
 * host and current pairing code:
 *
 *   gradle :app:connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.agentHost=192.168.0.200 \
 *       -Pandroid.testInstrumentationRunnerArguments.pairingCode=123456
 *
 * Without those arguments the test is skipped so CI stays green.
 */
@RunWith(AndroidJUnit4::class)
class AgentE2eTest {

    @Test
    fun pairThenReconnectWithSavedTokenOverWss() = runTest {
        val args = InstrumentationRegistry.getArguments()
        val host = args.getString("agentHost")
        val pairingCode = args.getString("pairingCode")
        if (host == null || pairingCode == null) {
            println("AgentE2eTest: skipped (no agentHost/pairingCode arguments)")
            return@runTest
        }

        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = context.applicationContext.getSharedPreferences("e2e_prefs", Context.MODE_PRIVATE)
        prefs.edit().clear().apply()

        val tokenStore = TokenStore(prefs)
        val pinStore = PinStore(prefs)

        // 1) First-time pairing over WSS with the code.
        val first = RemoteConnection(tokenStore, pinStore)
        assertTrue("pairing with code must reach CONNECTED", first.connectUntil(host, pairingCode, timeoutSeconds = 15))
        assertNotNull("token must be persisted after pairing", tokenStore.getToken(host))
        assertNotNull("server-cert pin must be captured on first handshake", pinStore.getPin(host))
        first.disconnect()
        Thread.sleep(200)

        // 2) Returning connect with token only — proves TOFU pinning + token auth.
        val second = RemoteConnection(tokenStore, pinStore)
        assertTrue("token-only reconnect must reach CONNECTED", second.connectUntil(host, null, timeoutSeconds = 15))
        second.disconnect()
    }

    private fun RemoteConnection.connectUntil(host: String, pairingCode: String?, timeoutSeconds: Long): Boolean {
        val latch = CountDownLatch(1)
        val job = kotlinx.coroutines.MainScope().launch {
            state.collect { if (it == ConnectionState.CONNECTED) latch.countDown() }
        }
        connect(host, pairingCode = pairingCode)
        val reached = latch.await(timeoutSeconds, TimeUnit.SECONDS)
        job.cancel()
        return reached
    }
}