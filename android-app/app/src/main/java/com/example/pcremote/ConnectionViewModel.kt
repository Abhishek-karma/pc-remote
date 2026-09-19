package com.example.pcremote

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import com.example.pcremote.network.EncryptedPrefs
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.SettingsStore
import com.example.pcremote.network.TokenStore

/**
 * Hosts connection state, stores, and the RemoteConnection instance so they
 * survive Activity configuration changes (rotation, locale switch, etc.).
 */
class ConnectionViewModel(app: Application) : AndroidViewModel(app) {

    private val prefs = EncryptedPrefs.create(app)
    val tokenStore = TokenStore(prefs)
    val settingsStore = SettingsStore(prefs)
    val pinStore = PinStore(prefs)
    val connection = RemoteConnection(tokenStore, pinStore) { host, name ->
        settingsStore.setName(host, name)
    }

    override fun onCleared() {
        super.onCleared()
        connection.shutdown()
    }
}
