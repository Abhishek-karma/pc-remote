package com.example.pcremote.network

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * DiscoveryService logic against a fake NsdGateway — no real networking
 * (per 11-TESTING-STRATEGY.md). Posting is injected synchronously so
 * snapshot state is observable directly after each callback.
 */
class DiscoveryServiceTest {

    private class FakeGateway : NsdGateway {
        var events: DiscoveryEvents? = null
        var failOnStart = false
        var stopCalls = 0
        var resolveResult: DiscoveredPc? = DiscoveredPc("PC-A", "PC-A", "192.168.1.5", 58642)
        val resolvedNames = mutableListOf<String>()

        override fun discoverServices(serviceType: String, events: DiscoveryEvents) {
            this.events = events
            if (failOnStart) events.onStartFailed()
        }

        override fun stopDiscovery() {
            stopCalls++
        }

        override fun resolve(serviceName: String, onResolved: (DiscoveredPc?) -> Unit) {
            resolvedNames += serviceName
            resolveResult?.let(onResolved)
        }
    }

    private class FakeMulticastGate : MulticastGate {
        var acquires = 0
        var releases = 0
        override fun acquire() {
            acquires++
        }

        override fun release() {
            releases++
        }
    }

    private fun service(gateway: FakeGateway = FakeGateway(), gate: FakeMulticastGate = FakeMulticastGate()) =
        DiscoveryService(gateway, gate, post = { it() })

    @Test
    fun `found service is resolved and added to the list`() {
        val gateway = FakeGateway()
        val s = service(gateway)
        s.start()

        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)

        assertEquals(listOf(DiscoveredPc("PC-A", "PC-A", "192.168.1.5", 58642)), s.snapshot.value.pcs)
        assertEquals(listOf("PC-A"), gateway.resolvedNames)
        s.stop()
    }

    @Test
    fun `duplicate found is ignored`() {
        val gateway = FakeGateway()
        val s = service(gateway)
        s.start()

        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)
        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)

        assertEquals(1, s.snapshot.value.pcs.size)
        assertEquals(1, gateway.resolvedNames.size)
        s.stop()
    }

    @Test
    fun `found service of another type is ignored`() {
        val gateway = FakeGateway()
        val s = service(gateway)
        s.start()

        gateway.events!!.onFound("sneaky", "_other._tcp.")

        assertTrue(s.snapshot.value.pcs.isEmpty())
        assertTrue(gateway.resolvedNames.isEmpty())
        s.stop()
    }

    @Test
    fun `found service with local suffix is accepted`() {
        val gateway = FakeGateway()
        val s = service(gateway)
        s.start()

        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE + "local.")

        assertEquals(1, s.snapshot.value.pcs.size)
        s.stop()
    }

    @Test
    fun `lost service is removed from the list`() {
        val gateway = FakeGateway()
        val s = service(gateway)
        s.start()

        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)
        gateway.events!!.onLost("PC-A")

        assertEquals(emptyList(), s.snapshot.value.pcs)
        s.stop()
    }

    @Test
    fun `failed resolve is skipped and a later announcement retries`() {
        val gateway = FakeGateway().apply { resolveResult = null }
        val s = service(gateway)
        s.start()

        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)
        assertTrue(s.snapshot.value.pcs.isEmpty())

        // Next announcement: resolve now succeeds.
        gateway.resolveResult = DiscoveredPc("PC-A", "PC-A", "192.168.1.5", 58642)
        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)

        assertEquals(1, s.snapshot.value.pcs.size)
        s.stop()
    }

    @Test
    fun `stop clears the list and releases the multicast gate`() {
        val gateway = FakeGateway()
        val gate = FakeMulticastGate()
        val s = service(gateway, gate)
        s.start()
        assertEquals(1, gate.acquires)

        gateway.events!!.onFound("PC-A", DiscoveryService.SERVICE_TYPE)
        s.stop()

        assertEquals(1, gateway.stopCalls)
        assertEquals(1, gate.releases)
        assertEquals(DiscoveryStatus.IDLE, s.snapshot.value.status)
        assertTrue(s.snapshot.value.pcs.isEmpty())
    }

    @Test
    fun `start failure reports FAILED`() {
        val gateway = FakeGateway().apply { failOnStart = true }
        val gate = FakeMulticastGate()
        val s = service(gateway, gate)
        s.start()

        assertEquals(DiscoveryStatus.FAILED, s.snapshot.value.status)
        s.stop()
    }
}