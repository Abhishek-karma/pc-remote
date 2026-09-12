package com.example.pcremote.ui

import android.content.Context
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.example.pcremote.network.DiscoveredPc
import com.example.pcremote.network.DiscoveryEvents
import com.example.pcremote.network.DiscoveryService
import com.example.pcremote.network.MulticastGate
import com.example.pcremote.network.NsdGateway
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.TokenStore
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Compose UI tests for the discovery section of the Pairing screen, run
 * against a fake DiscoveryService (no real NsdManager / no network — per
 * 11-TESTING-STRATEGY.md).
 */
@RunWith(AndroidJUnit4::class)
class PairingScreenTest {

    @get:Rule
    val composeTestRule = createComposeRule()

    private class FakeGateway : NsdGateway {
        var events: DiscoveryEvents? = null
        override fun discoverServices(serviceType: String, events: DiscoveryEvents) {
            this.events = events
        }

        override fun stopDiscovery() {}

        override fun resolve(serviceName: String, onResolved: (DiscoveredPc?) -> Unit) {
            onResolved(DiscoveredPc(serviceName, "My Desktop PC", "192.168.1.5", 58642))
        }
    }

    private class FakeMulticastGate : MulticastGate {
        override fun acquire() {}
        override fun release() {}
    }

    private fun fakeDiscovery(gateway: FakeGateway) =
        DiscoveryService(gateway, FakeMulticastGate(), post = { it() })

    private val prefs = ApplicationProvider.getApplicationContext<Context>()
        .getSharedPreferences("pairing_test_prefs", Context.MODE_PRIVATE)

    @Test
    fun discoveredPcIsListedAndTapPrefillsTheIpField() {
        val gateway = FakeGateway()
        composeTestRule.setContent {
            PairingScreen(
                connection = RemoteConnection(TokenStore(prefs), PinStore(prefs)),
                tokenStore = TokenStore(prefs),
                onConnected = {},
                discoveryProvider = { fakeDiscovery(gateway) }
            )
        }

        gateway.events!!.onFound("DESKTOP-ABC", DiscoveryService.SERVICE_TYPE)

        composeTestRule.onNodeWithText("My Desktop PC").assertIsDisplayed()
        composeTestRule.onNodeWithText("My Desktop PC").performClick()

        // No saved token for this host, so only the IP field is prefilled —
        // pairing still has to go through the code flow.
        composeTestRule.onNodeWithText("192.168.1.5").assertIsDisplayed()
    }

    @Test
    fun noPcsFoundHintAppearsAfterTheSearchGracePeriod() {
        val gateway = FakeGateway() // nothing ever resolves
        composeTestRule.setContent {
            PairingScreen(
                connection = RemoteConnection(TokenStore(prefs), PinStore(prefs)),
                tokenStore = TokenStore(prefs),
                onConnected = {},
                discoveryProvider = { fakeDiscovery(gateway) }
            )
        }

        composeTestRule.mainClock.advanceTimeBy(6_000)

        composeTestRule.onNodeWithText(
            "No PCs found — make sure both devices are on the same Wi-Fi " +
                "and the agent is running."
        ).assertIsDisplayed()
    }
}