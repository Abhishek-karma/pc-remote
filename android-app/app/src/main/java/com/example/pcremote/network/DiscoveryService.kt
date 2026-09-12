package com.example.pcremote.network

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** A PC found on the LAN via mDNS ("_pc-remote._tcp" — see 07-API-SPECIFICATION.md §7). */
data class DiscoveredPc(
    val serviceName: String,
    val displayName: String,
    val host: String,
    val port: Int
)

enum class DiscoveryStatus { IDLE, SEARCHING, FAILED }

data class DiscoverySnapshot(val status: DiscoveryStatus, val pcs: List<DiscoveredPc>)

/** Callbacks surfaced by the browse (framework-agnostic so it is unit-testable). */
interface DiscoveryEvents {
    fun onFound(serviceName: String, serviceType: String)
    fun onLost(serviceName: String)
    fun onStartFailed()
}

/**
 * Testability seam for the subset of NsdManager the service uses. The real
 * implementation translates NsdManager events into plain strings / `DiscoveredPc`.
 */
interface NsdGateway {
    fun discoverServices(serviceType: String, events: DiscoveryEvents)
    fun stopDiscovery()
    /** Resolves host/port/TXT for a found service; invokes [onResolved] with null on failure. */
    fun resolve(serviceName: String, onResolved: (DiscoveredPc?) -> Unit)
}

/** Testability seam for WifiManager.MulticastLock. */
interface MulticastGate {
    fun acquire()
    fun release()
}

/**
 * Browses the LAN for PCs running the Windows agent. Wraps NsdManager behind
 * [NsdGateway] so the logic is unit-testable with fakes (see
 * 11-TESTING-STRATEGY.md). Holds a MulticastLock while browsing
 * (CHANGE_WIFI_MULTICAST_STATE, a normal auto-granted permission) so multicast
 * replies are received. Scoped to the caller's lifecycle: discovery runs only
 * while [start] is current; the pairing screen starts it on entry and stops it
 * on exit.
 *
 * UI-facing state is always touched via [post] (the main looper in production)
 * because NsdManager's callbacks arrive on an internal thread.
 */
class DiscoveryService(
    private val gateway: NsdGateway,
    private val multicastGate: MulticastGate,
    private val post: (() -> Unit) -> Unit
) {
    companion object {
        /** Must match the agent's ServiceType ("_pc-remote._tcp." + ".local."). */
        const val SERVICE_TYPE: String = "_pc-remote._tcp."

        fun create(context: Context): DiscoveryService {
            val nsd = context.applicationContext.getSystemService(Context.NSD_SERVICE) as NsdManager
            val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            val lock = wifi.createMulticastLock("pc-remote Discovery")
            lock.setReferenceCounted(false)
            val mainHandler = Handler(Looper.getMainLooper())
            return DiscoveryService(
                AndroidNsdGateway(nsd),
                AndroidMulticastGate(lock),
                post = { mainHandler.post(it) }
            )
        }
    }

    private val _snapshot = MutableStateFlow(DiscoverySnapshot(DiscoveryStatus.IDLE, emptyList()))
    val snapshot: StateFlow<DiscoverySnapshot> = _snapshot

    private var browsing = false

    /** Starts (or restarts) the browse; resets the list and status. */
    @Synchronized
    fun start() {
        if (browsing) stopInternal()

        _snapshot.value = DiscoverySnapshot(DiscoveryStatus.SEARCHING, emptyList())
        val events = object : DiscoveryEvents {
            override fun onFound(serviceName: String, serviceType: String) {
                // Tolerate the trailing ".local." some platforms append to the
                // advertised service type.
                if (serviceType != SERVICE_TYPE && serviceType != SERVICE_TYPE + "local.") return
                // NsdManager re-delivers found services on repeat announcements;
                // ignore ones we already have (dedupe runs before resolve, so a
                // slow resolve can't create a duplicate).
                if (_snapshot.value.pcs.any { it.serviceName == serviceName }) return
                gateway.resolve(serviceName) { resolved ->
                    if (resolved == null) return@resolve // failed/timed out; next announcement retries
                    post {
                        _snapshot.value = _snapshot.value.copy(pcs = _snapshot.value.pcs + resolved)
                    }
                }
            }

            override fun onLost(serviceName: String) {
                post {
                    _snapshot.value = _snapshot.value.copy(
                        pcs = _snapshot.value.pcs.filterNot { it.serviceName == serviceName }
                    )
                }
            }

            override fun onStartFailed() = this@DiscoveryService.onStartFailed()
        }

        browsing = true
        gateway.discoverServices(SERVICE_TYPE, events)
        // Hold the multicast lock for the whole browse so the agent's
        // multicast replies are received (CHANGE_WIFI_MULTICAST_STATE).
        multicastGate.acquire()
    }

    /** Stops the browse, releases the multicast lock, clears the list. */
    @Synchronized
    fun stop() = stopInternal()

    private fun stopInternal() {
        if (browsing) {
            gateway.stopDiscovery()
            multicastGate.release()
        }
        browsing = false
        _snapshot.value = DiscoverySnapshot(DiscoveryStatus.IDLE, emptyList())
    }

    private fun onStartFailed() {
        post {
            _snapshot.value = DiscoverySnapshot(DiscoveryStatus.FAILED, emptyList())
        }
    }
}

private class AndroidNsdGateway(private val nsd: NsdManager) : NsdGateway {
    private var listener: NsdManager.DiscoveryListener? = null

    // The framework-delivered NsdServiceInfo per service name. Resolving with
    // this exact object passes NsdManager's internal validation — reconstructing
    // an NsdServiceInfo from name+type is rejected ("Service type cannot be
    // empty") on some Android versions.
    private val foundServices = mutableMapOf<String, NsdServiceInfo>()

    override fun discoverServices(serviceType: String, events: DiscoveryEvents) {
        val l = object : NsdManager.DiscoveryListener {
            override fun onServiceFound(info: NsdServiceInfo) {
                foundServices[info.serviceName] = info
                events.onFound(info.serviceName, info.serviceType)
            }

            override fun onServiceLost(info: NsdServiceInfo) {
                foundServices.remove(info.serviceName)
                events.onLost(info.serviceName)
            }

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) =
                events.onStartFailed()

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
        }
        listener = l
        nsd.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, l)
    }

    override fun stopDiscovery() {
        listener?.let { nsd.stopServiceDiscovery(it) }
        listener = null
        foundServices.clear()
    }

    override fun resolve(serviceName: String, onResolved: (DiscoveredPc?) -> Unit) {
        val info = foundServices[serviceName] ?: return onResolved(null)
        nsd.resolveService(info, object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) = onResolved(null)

            override fun onServiceResolved(resolved: NsdServiceInfo) {
                val host = resolved.host?.hostAddress ?: return onResolved(null)
                val txtName = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    resolved.attributes?.get("name")?.let { String(it, Charsets.UTF_8) }
                } else {
                    null
                }
                onResolved(
                    DiscoveredPc(
                        serviceName = resolved.serviceName,
                        displayName = txtName ?: resolved.serviceName,
                        host = host,
                        port = resolved.port
                    )
                )
            }
        })
    }
}

private class AndroidMulticastGate(private val lock: WifiManager.MulticastLock) : MulticastGate {
    override fun acquire() = lock.acquire()
    override fun release() = lock.release()
}