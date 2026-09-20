package com.example.pcremote.network

import android.content.Context
import android.content.Intent
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import okhttp3.Request

data class AndroidUpdateResult(
    val hasUpdate: Boolean,
    val latestVersion: String,
    val apkUrl: String,
    val releaseNotes: String
)

object UpdateChecker {
    private const val API_URL = "https://api.github.org/repos/Abhishek-karma/pc-remote/releases/latest"
    private val client = OkHttpClient()
    private val json = Json { ignoreUnknownKeys = true }

    suspend fun checkForUpdate(currentVersionName: String): AndroidUpdateResult = withContext(Dispatchers.IO) {
        try {
            val request = Request.Builder()
                .url(API_URL)
                .header("User-Agent", "PCRemoteAndroid")
                .build()

            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) return@withContext AndroidUpdateResult(false, "", "", "")
                val bodyStr = response.body?.string() ?: return@withContext AndroidUpdateResult(false, "", "", "")
                val jsonDoc = json.parseToJsonElement(bodyStr).jsonObject

                val tagName = jsonDoc["tag_name"]?.jsonPrimitive?.content ?: ""
                val notes = jsonDoc["body"]?.jsonPrimitive?.content ?: ""

                var apkUrl = ""
                jsonDoc["assets"]?.jsonArray?.forEach { asset ->
                    val name = asset.jsonObject["name"]?.jsonPrimitive?.content ?: ""
                    if (name.endsWith(".apk", ignoreCase = true)) {
                        apkUrl = asset.jsonObject["browser_download_url"]?.jsonPrimitive?.content ?: ""
                    }
                }

                val current = currentVersionName.trimStart('v', 'V')
                val latest = tagName.trimStart('v', 'V')

                val isNewer = latest.isNotBlank() && !current.equals(latest, ignoreCase = true) && apkUrl.isNotBlank()

                AndroidUpdateResult(isNewer, tagName, apkUrl, notes)
            }
        } catch (_: Exception) {
            AndroidUpdateResult(false, "", "", "")
        }
    }

    fun openUpdateUrl(context: Context, url: String) {
        try {
            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(url)).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        } catch (_: Exception) {}
    }
}
