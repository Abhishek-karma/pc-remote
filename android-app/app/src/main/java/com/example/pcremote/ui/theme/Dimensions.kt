package com.example.pcremote.ui.theme

import androidx.compose.material3.Shapes
import androidx.compose.ui.unit.dp

/** Spacing scale — 4pt grid; screens must not invent values outside it. */
object Spacing {
    val xs = 4.dp
    val sm = 8.dp
    val md = 12.dp
    val lg = 16.dp
    val xl = 20.dp
    val xxl = 24.dp
    val xxxl = 32.dp
}

/** Consistent corner language: controls small, containers medium, sheets large. */
object Corners {
    val small = 8.dp
    val medium = 12.dp
    val large = 16.dp
}

val AppShapes = Shapes(
    extraSmall = androidx.compose.foundation.shape.RoundedCornerShape(Corners.small),
    small = androidx.compose.foundation.shape.RoundedCornerShape(Corners.small),
    medium = androidx.compose.foundation.shape.RoundedCornerShape(Corners.medium),
    large = androidx.compose.foundation.shape.RoundedCornerShape(Corners.large),
    extraLarge = androidx.compose.foundation.shape.RoundedCornerShape(Corners.large)
)
