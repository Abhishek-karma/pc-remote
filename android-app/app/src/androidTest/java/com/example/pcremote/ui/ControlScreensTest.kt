package com.example.pcremote.ui

import android.content.Context
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.example.pcremote.network.PinStore
import com.example.pcremote.network.RemoteConnection
import com.example.pcremote.network.TokenStore
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Basic Compose checks for the control screens (11-TESTING-STRATEGY.md):
 * modifier-lock state and the Shutdown confirmation dialog. The
 * RemoteConnection here never connects, so sends are harmless no-ops.
 */
@RunWith(AndroidJUnit4::class)
class ControlScreensTest {

    @get:Rule
    val composeTestRule = createComposeRule()

    private val connection = RemoteConnection(
        TokenStore(
            ApplicationProvider.getApplicationContext<Context>()
                .getSharedPreferences("controls_test_prefs", Context.MODE_PRIVATE)
        ),
        PinStore(
            ApplicationProvider.getApplicationContext<Context>()
                .getSharedPreferences("controls_test_prefs", Context.MODE_PRIVATE)
        )
    )

    @Test
    fun modifierLockTogglesAndClears() {
        composeTestRule.setContent { KeyboardScreen(connection) }

        composeTestRule.onNodeWithText("CTRL").performClick()
        composeTestRule.onNodeWithText("CTRL", substring = true).assertIsSelected()

        composeTestRule.onNodeWithText("CTRL", substring = true).performClick()
        composeTestRule.onNodeWithText("CTRL", substring = true).assertIsNotSelected()
    }

    @Test
    fun shutdownShowsDialogAndCancelDismissesIt() {
        composeTestRule.setContent { PowerScreen(connection, pcName = "TEST-PC", onFeedback = {}) }

        composeTestRule.onNodeWithText("Shut Down").performClick()
        composeTestRule.onNodeWithText("Shut Down PC?").assertIsDisplayed()

        composeTestRule.onNodeWithText("Cancel").performClick()
        composeTestRule.onNodeWithText("Shut Down PC?").assertDoesNotExist()
    }

    @Test
    fun sleepActsImmediatelyWithoutDialog() {
        composeTestRule.setContent { PowerScreen(connection, pcName = "TEST-PC", onFeedback = {}) }

        composeTestRule.onNodeWithText("Sleep").performClick()
        composeTestRule.onNodeWithText("Sleep PC?").assertDoesNotExist()
    }
}