plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.serialization")
}

android {
    namespace = "com.example.pcremote"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.example.pcremote"
        minSdk = 26
        targetSdk = 35
        // CI injects these from the git tag.
        versionCode = (project.findProperty("versionCode") as String?)?.toInt() ?: 1
        versionName = (project.findProperty("versionName") as String?) ?: "0.2.2"
    }

    signingConfigs {
        create("release") {
            // Signing material comes from env vars only — never committed
            // (ANDROID_KEYSTORE_FILE/PASSWORD, ANDROID_KEY_ALIAS/KEY_PASSWORD).
            // Unset → release builds are unsigned.
            val storePath = System.getenv("ANDROID_KEYSTORE_FILE")
            if (!storePath.isNullOrBlank()) {
                storeFile = file(storePath)
                storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ANDROID_KEY_ALIAS")
                keyPassword = System.getenv("ANDROID_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (!System.getenv("ANDROID_KEYSTORE_FILE").isNullOrBlank()) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }

    // Product-friendly APK names (PC-Remote-Android-release.apk).
    base {
        archivesName = "PC-Remote-Android"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
    }
    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.14"
    }

    testOptions {
        // android.jar framework stubs return defaults in local unit tests;
        // the tested logic (gestures, text diff, backoff) is pure Kotlin, but
        // keep this on for safety.
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.9.2")

    // The transport and the wire format.
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")

    // Keystore-backed storage for trust tokens and certificate pins.
    implementation("androidx.security:security-crypto:1.1.0")

    testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
    testImplementation("org.jetbrains.kotlin:kotlin-test-junit5:1.9.24")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}
tasks.withType<Test>().configureEach {
    useJUnitPlatform()
}