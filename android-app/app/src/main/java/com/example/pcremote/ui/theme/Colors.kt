package com.example.pcremote.ui.theme

import androidx.compose.ui.graphics.Color

/**
 * App palette. Dark-first (04-UI-UX-SPECIFICATION.md §3): near-black
 * blue-steel neutrals carry ~90% of the UI; the warm amber accent is reserved
 * for interactive/selected elements only (60/30/10). Light text ≥ 12:1
 * contrast on these surfaces (WCAG AA).
 */
object RemoteColors {
    // Neutrals (60%)
    val Background = Color(0xFF0E1216)
    val Surface = Color(0xFF141920)
    val SurfaceVariant = Color(0xFF1D242E)
    val SurfaceContainer = Color(0xFF1A2129)
    val Outline = Color(0xFF39434F)
    val OnBackground = Color(0xFFE6EAEF)
    val OnSurface = Color(0xFFE6EAEF)
    val OnSurfaceVariant = Color(0xFF9AA6B4)

    // Accent (10%) — interactive/selected only.
    val Accent = Color(0xFFE8B45A)
    val OnAccent = Color(0xFF241B05)
    val AccentContainer = Color(0xFF463812)
    val OnAccentContainer = Color(0xFFF3DFB4)

    // Secondary neutrals for chips/containers.
    val SecondaryContainer = Color(0xFF232B36)
    val OnSecondaryContainer = Color(0xFFCDD8E5)

    // Status.
    val Positive = Color(0xFF7CC98A)
    val Error = Color(0xFFE27A7A)
    val OnError = Color(0xFF2B0A0A)
    val ErrorContainer = Color(0xFF47201F)
    val OnErrorContainer = Color(0xFFF3D4D0)
}
