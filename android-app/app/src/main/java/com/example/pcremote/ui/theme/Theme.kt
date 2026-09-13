package com.example.pcremote.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.material3.Typography
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

private val RemoteDarkScheme = darkColorScheme(
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

private val RemoteLightScheme = lightColorScheme(
    primary = RemoteColors.LightAccent,
    onPrimary = RemoteColors.LightOnAccent,
    primaryContainer = RemoteColors.LightAccentContainer,
    onPrimaryContainer = RemoteColors.LightOnAccentContainer,
    secondaryContainer = RemoteColors.LightSecondaryContainer,
    onSecondaryContainer = RemoteColors.LightOnSecondaryContainer,
    background = RemoteColors.LightBackground,
    onBackground = RemoteColors.LightOnBackground,
    surface = RemoteColors.LightSurface,
    onSurface = RemoteColors.LightOnSurface,
    surfaceVariant = RemoteColors.LightSurfaceVariant,
    onSurfaceVariant = RemoteColors.LightOnSurfaceVariant,
    surfaceContainer = RemoteColors.LightSurfaceContainer,
    surfaceContainerHigh = RemoteColors.LightSurfaceVariant,
    outline = RemoteColors.LightOutline,
    error = RemoteColors.LightError,
    onError = RemoteColors.LightOnError,
    errorContainer = RemoteColors.LightErrorContainer,
    onErrorContainer = RemoteColors.LightOnErrorContainer
)

/** Theme-dependent status colors beyond the Material scheme (success/pending). */
data class StatusPalette(val positive: Color, val pending: Color)

private val DarkStatusPalette = StatusPalette(RemoteColors.Positive, RemoteColors.Warning)
private val LightStatusPalette = StatusPalette(RemoteColors.LightPositive, Color(0xFFD97706))

val LocalStatusPalette = staticCompositionLocalOf { DarkStatusPalette }

@Composable
fun statusPositive() = LocalStatusPalette.current.positive

@Composable
fun statusPending() = LocalStatusPalette.current.pending

/**
 * Two weights, no custom font family — hierarchy comes from size/weight/
 * opacity within Material's type scale. Screens must use these roles, not
 * ad-hoc sizes.
 */
private val RemoteType = Typography()

@Composable
fun RemoteTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = if (darkTheme) RemoteDarkScheme else RemoteLightScheme,
        typography = RemoteType,
        shapes = AppShapes
    ) {
        CompositionLocalProvider(LocalStatusPalette provides if (darkTheme) DarkStatusPalette else LightStatusPalette) {
            content()
        }
    }
}
