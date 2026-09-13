package com.example.pcremote.ui.theme

import androidx.compose.ui.graphics.Color

/**
 * App palette: Dark-first, precision technical remote utility.
 * Obsidian and titanium neutrals paired with electric cyan accent and
 * high-contrast status tokens (Emerald Connected, Amber Pending, Crimson Danger).
 */
object RemoteColors {
    // Deep Obsidian Neutrals (Dark Theme)
    val Background = Color(0xFF090D14)
    val Surface = Color(0xFF0F1520)
    val SurfaceVariant = Color(0xFF161F2E)
    val SurfaceContainer = Color(0xFF121926)
    val SurfaceContainerHigh = Color(0xFF1A2436)
    val Outline = Color(0xFF26354B)
    val OutlineVariant = Color(0xFF1B2637)
    val OnBackground = Color(0xFFF1F5F9)
    val OnSurface = Color(0xFFE2E8F0)
    val OnSurfaceVariant = Color(0xFF94A3B8)

    // Accent: Electric Ice Blue / Precision Cyan
    val Accent = Color(0xFF38BDF8)
    val OnAccent = Color(0xFF031627)
    val AccentContainer = Color(0xFF0C2D48)
    val OnAccentContainer = Color(0xFFBAE6FD)

    // Secondary neutrals for chips/containers
    val SecondaryContainer = Color(0xFF1A2333)
    val OnSecondaryContainer = Color(0xFFCBD5E1)

    // Semantic Status Colors
    val Positive = Color(0xFF34D399) // Emerald for Connected
    val PositiveContainer = Color(0xFF064E3B)
    val Warning = Color(0xFFFBBF24)  // Amber for Reconnecting
    val WarningContainer = Color(0xFF78350F)
    val Error = Color(0xFFF43F5E)    // Crimson/Rose for Destructive & Error
    val OnError = Color(0xFFFFFFFF)
    val ErrorContainer = Color(0xFF4C0519)
    val OnErrorContainer = Color(0xFFFFE4E6)

    // Key & Control Surfaces
    val KeyBackground = Color(0xFF141C2B)
    val KeyBorder = Color(0xFF223047)
    val KeyActiveBackground = Color(0xFF0369A1)
    val KeyActiveBorder = Color(0xFF38BDF8)

    // Light theme fallback (WCAG AA compliant)
    val LightBackground = Color(0xFFF8FAFC)
    val LightSurface = Color(0xFFFFFFFF)
    val LightSurfaceVariant = Color(0xFFE2E8F0)
    val LightSurfaceContainer = Color(0xFFF1F5F9)
    val LightOutline = Color(0xFFCBD5E1)
    val LightOnBackground = Color(0xFF0F172A)
    val LightOnSurface = Color(0xFF0F172A)
    val LightOnSurfaceVariant = Color(0xFF475569)
    val LightAccent = Color(0xFF0284C7)
    val LightOnAccent = Color(0xFFFFFFFF)
    val LightAccentContainer = Color(0xFFE0F2FE)
    val LightOnAccentContainer = Color(0xFF0369A1)
    val LightSecondaryContainer = Color(0xFFE2E8F0)
    val LightOnSecondaryContainer = Color(0xFF1E293B)
    val LightPositive = Color(0xFF059669)
    val LightError = Color(0xFFE11D48)
    val LightOnError = Color(0xFFFFFFFF)
    val LightErrorContainer = Color(0xFFFFE4E6)
    val LightOnErrorContainer = Color(0xFF881337)
}

