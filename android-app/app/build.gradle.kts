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
        // CI injects these from the Git tag (docs/15-DEPLOYMENT.md §3).
        versionCode = (project.findProperty("versionCode") as String?)?.toInt() ?: 6
        versionName = (project.findProperty("versionName") as String?) ?: "0.1.5"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        create("release") {
            // Signing material comes from env vars only — never committed
            // (ANDROID_KEYSTORE_FILE/PASSWORD, ANDROID_KEY_ALIAS/KEY_PASSWORD;
            // docs/15-DEPLOYMENT.md §5). Unset → release builds are unsigned.
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
        // android.jar framework stubs return defaults in local unit tests
        // (DiscoveryService runs against injected fakes, so no VM bytes are
        // executed, but keep this on for safety).
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    // Proper vector icons for the nav bar / controls (revisit size impact if R8 is enabled).
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.6")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")
    // Encrypted storage for trust tokens + cert pins (09-SECURITY-PRIVACY.md §4).
    implementation("androidx.security:security-crypto:1.1.0")

    // Unit tests: JUnit 5 per docs/11-TESTING-STRATEGY.md §1.
    testImplementation("org.junit.jupiter:junit-jupiter:5.10.2")
    testImplementation("org.jetbrains.kotlin:kotlin-test-junit5:1.9.24")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")

    // Instrumented/Compose tests (run on emulator, fake backends, no network).
    androidTestImplementation(composeBom)
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:core:1.6.1")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}

tasks.withType<Test>().configureEach {
    useJUnitPlatform()
}