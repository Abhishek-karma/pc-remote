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

    // Light theme — same roles, adjusted for light surfaces: the amber accent
    // darkens to hold contrast on near-white backgrounds (WCAG AA).
    val LightBackground = Color(0xFFF4F6F9)
    val LightSurface = Color(0xFFFFFFFF)
    val LightSurfaceVariant = Color(0xFFE3E9F0)
    val LightSurfaceContainer = Color(0xFFEAEFF4)
    val LightOutline = Color(0xFFB7C1CC)
    val LightOnBackground = Color(0xFF1A212B)
    val LightOnSurface = Color(0xFF1A212B)
    val LightOnSurfaceVariant = Color(0xFF4B5765)
    val LightAccent = Color(0xFF7C5A10)
    val LightOnAccent = Color(0xFFFFFFFF)
    val LightAccentContainer = Color(0xFFF2E2BC)
    val LightOnAccentContainer = Color(0xFF33280A)
    val LightSecondaryContainer = Color(0xFFDDE5EE)
    val LightOnSecondaryContainer = Color(0xFF253141)
    val LightPositive = Color(0xFF2E7D46)
    val LightError = Color(0xFFA93B32)
    val LightOnError = Color(0xFFFFFFFF)
    val LightErrorContainer = Color(0xFFF6DAD6)
    val LightOnErrorContainer = Color(0xFF4A1512)
}
