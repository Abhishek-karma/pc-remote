package com.example.pcremote.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * App-wide palette. The app is a "dim room, in front of the TV" tool
 * (04-UI-UX-SPECIFICATION.md §3), so the theme is dark-first, always —
 * never follows the system setting. Neutrals are blue-steel; the single
 * accent is a warm amber kept for interactive elements only. Contrast for
 * text on these surfaces meets WCAG AA (light text ≈ #E6EAEF on the
 * #10141A family reads at ~13:1).
 */
private val RemoteColors = darkColorScheme(
    primary = Color(0xFFE8B45A),
    onPrimary = Color(0xFF221A05),
    primaryContainer = Color(0xFF4A3A12),
    onPrimaryContainer = Color(0xFFF5E2BA),
    secondary = Color(0xFF9AA8BB),
    onSecondary = Color(0xFF10161D),
    secondaryContainer = Color(0xFF232B36),
    onSecondaryContainer = Color(0xFFCDD8E5),
    background = Color(0xFF10141A),
    onBackground = Color(0xFFE6EAEF),
    surface = Color(0xFF141920),
    onSurface = Color(0xFFE6EAEF),
    surfaceVariant = Color(0xFF1E2530),
    onSurfaceVariant = Color(0xFFA7B2C0),
    outline = Color(0xFF3A4552),
    error = Color(0xFFE27A7A),
    onError = Color(0xFF2B0A0A),
    errorContainer = Color(0xFF4A1D1D),
    onErrorContainer = Color(0xFFF5D3D3)
)

@Composable
fun RemoteTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = RemoteColors,
        content = content
    )
}