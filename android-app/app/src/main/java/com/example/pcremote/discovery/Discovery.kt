package com.example.pcremote.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** A PC advertising itself on the LAN. */
data class DiscoveredPc(
    val serviceName: String,
    val name: String,
    val host: String,
    val port: Int,
    /** The PC's stable identity from its mDNS TXT record. Lets the app use a
     *  saved token on the very first connect to an address it has never seen. */
    val pcId: String? = null,
)

enum class DiscoveryStatus { SEARCHING, FAILED }

/**
 * Finds PCs on the same Wi-Fi using mDNS, so the user never types an IP address.
 *
 * Discovery is a convenience: if it fails, Settings still offers manual entry.
 * That is why a failure here is a status to show rather than an error to raise.
 */
class Discovery(context: Context) {

    companion object {
        /** Must match MdnsAdvertiser.ServiceType on the Windows side. */
        private const val SERVICE_TYPE = "_pc-remote._tcp."
        private const val TAG = "Discovery"
    }

    private val nsd = context.applicationContext.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val multicast = (context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager)
        .createMulticastLock("pc-remote")
        .apply { setReferenceCounted(false) }

    private val _pcs = MutableStateFlow<List<DiscoveredPc>>(emptyList())
    val pcs: StateFlow<List<DiscoveredPc>> = _pcs

    private val _status = MutableStateFlow<DiscoveryStatus?>(null)
    val status: StateFlow<DiscoveryStatus?> = _status

    private var listener: NsdManager.DiscoveryListener? = null

    /** Names seen in a browse but not yet resolved. mDNS gives us a name first
     *  and the address only after an explicit resolve. */
    private val found = mutableMapOf<String, NsdServiceInfo>()

    private var resolving = false

    @Synchronized
    fun start() {
        stop()

        _status.value = DiscoveryStatus.SEARCHING
        _pcs.value = emptyList()
        found.clear()

        // Without this lock many routers silently drop our multicast replies.
        try {
            multicast.acquire()
        } catch (e: SecurityException) {
            Log.w(TAG, "multicast lock unavailable; discovery may miss PCs")
        }

        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(type: String) = Unit
            override fun onDiscoveryStopped(type: String) = Unit

            override fun onServiceFound(info: NsdServiceInfo) {
                synchronized(this@Discovery) { found[info.serviceName] = info }
                resolveNext()
            }

            override fun onServiceLost(info: NsdServiceInfo) {
                synchronized(this@Discovery) {
                    found.remove(info.serviceName)
                    _pcs.value = _pcs.value.filterNot { it.serviceName == info.serviceName }
                }
            }

            override fun onStartDiscoveryFailed(type: String, error: Int) {
                Log.w(TAG, "discovery failed to start (error $error)")
                _status.value = DiscoveryStatus.FAILED
                releaseMulticast()
            }

            override fun onStopDiscoveryFailed(type: String, error: Int) = Unit
        }

        listener = l
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, l)
    }

    @Synchronized
    fun stop() {
        listener?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        listener = null
        found.clear()
        resolving = false
        releaseMulticast()
    }

    private fun releaseMulticast() {
        try {
            if (multicast.isHeld) multicast.release()
        } catch (e: RuntimeException) {
            Log.w(TAG, "could not release the multicast lock")
        }
    }

    /** Resolves one service at a time: NsdManager tolerates only one resolve in
     *  flight and drops the rest, which silently loses PCs from the list. */
    @Synchronized
    private fun resolveNext() {
        if (resolving) return

        val name = found.keys.firstOrNull() ?: return
        val info = found[name] ?: return

        resolving = true
        nsd.resolveService(info, object : NsdManager.ResolveListener {
            override fun onResolveFailed(failed: NsdServiceInfo, error: Int) {
                Log.w(TAG, "could not resolve ${failed.serviceName} (error $error)")
                synchronized(this@Discovery) {
                    resolving = false
                    resolveNext()
                }
            }

            override fun onServiceResolved(resolved: NsdServiceInfo) {
                val address = resolved.host?.hostAddress?.substringBefore('%')
                if (address != null) {
                    fun attribute(key: String): String? =
                        resolved.attributes?.get(key)?.let { String(it, Charsets.UTF_8) }

                    val pc = DiscoveredPc(
                        serviceName = resolved.serviceName,
                        // TXT "name" is the human label; the instance name is
                        // truncated on some resolvers.
                        name = attribute("name") ?: resolved.serviceName,
                        host = address,
                        port = resolved.port,
                        pcId = attribute("pcid"),
                    )
                    _pcs.value = (_pcs.value.filterNot { it.serviceName == pc.serviceName } + pc)
                        .sortedBy { it.name.lowercase() }
                }

                synchronized(this@Discovery) {
                    resolving = false
                    resolveNext()
                }
            }
        })
    }
}