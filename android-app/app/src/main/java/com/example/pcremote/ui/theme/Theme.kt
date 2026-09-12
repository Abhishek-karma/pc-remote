package com.example.pcremote.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.Typography
import androidx.compose.runtime.Composable

private val RemoteScheme = darkColorScheme(
    primary = RemoteColors.Accent,
    onPrimary = RemoteColors.OnAccent,
    primaryContainer = RemoteColors.AccentContainer,
    onPrimaryContainer = RemoteColors.OnAccentContainer,
    secondaryContainer = RemoteColors.SecondaryContainer,
    onSecondaryContainer = RemoteColors.OnSecondaryContainer,
    background = RemoteColors.Background,
    onBackground = RemoteColors.OnBackground,
    surface = RemoteColors.Surface,
    onSurface = RemoteColors.OnSurface,
    surfaceVariant = RemoteColors.SurfaceVariant,
    onSurfaceVariant = RemoteColors.OnSurfaceVariant,
    surfaceContainer = RemoteColors.SurfaceContainer,
    surfaceContainerHigh = RemoteColors.SurfaceVariant,
    outline = RemoteColors.Outline,
    error = RemoteColors.Error,
    onError = RemoteColors.OnError,
    errorContainer = RemoteColors.ErrorContainer,
    onErrorContainer = RemoteColors.OnErrorContainer
)

/**
 * Two weights, no custom font family — hierarchy comes from size/weight/
 * opacity within Material's type scale (04 §3 "strong typography, minimal
 * decoration"). Screens must use these roles, not ad-hoc sizes.
 */
private val RemoteType = Typography()

@Composable
fun RemoteTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = RemoteScheme,
        typography = RemoteType,
        shapes = AppShapes,
        content = content
    )
}
