package com.example.pcremote.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder

/**
 * Keeps the control session alive while the user works in another app
 * (05-TECHNICAL-ARCHITECTURE.md §4 "planned additions"). Started when the
 * connection reaches CONNECTED and stopped when it drops; the socket itself
 * lives in RemoteConnection, this service only holds the process priority and
 * shows the ongoing session notification.
 */
class ConnectionForegroundService : Service() {

    companion object {
        const val CHANNEL_ID = "pc_remote_connection"
        const val NOTIFICATION_ID = 1
    }

    override fun onCreate() {
        super.onCreate()
        createChannel()
        startForeground(NOTIFICATION_ID, buildNotification())
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Active PC Remote session",
                NotificationManager.IMPORTANCE_LOW
            )
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    private fun buildNotification(): Notification {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_menu_compass)
                .setContentTitle("PC Remote")
                .setContentText("Controlling your PC")
                .setOngoing(true)
                .build()
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
                .setSmallIcon(android.R.drawable.ic_menu_compass)
                .setContentTitle("PC Remote")
                .setContentText("Controlling your PC")
                .setOngoing(true)
                .build()
        }
    }
}