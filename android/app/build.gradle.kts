plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("app.cash.paparazzi")
}

val keystore = rootProject.file("keystore/edgestpan-dev.keystore")
// CI may supply a different keystore's credentials; defaults match the dev keystore.
val ksStorePass = System.getenv("EDGESPAN_KS_PASS") ?: "edgestpan-dev"
val ksKeyPass = System.getenv("EDGESPAN_KEY_PASS") ?: ksStorePass
val ksAlias = System.getenv("EDGESPAN_KEY_ALIAS") ?: "edgestpan"

android {
    namespace = "com.edgestpan.app"
    compileSdk = 34
    defaultConfig {
        applicationId = "com.edgestpan.app"
        // Accessibility gestures (dispatchGesture) exist from API 24.
        minSdk = 24
        targetSdk = 34
        versionCode = 42
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        versionName = "0.9.23"
        buildConfigField("String", "BUILD_DATE", "\"2026-10-25\"")
        buildConfigField("int", "PROTOCOL", "1")
        ndk {
            abiFilters += listOf("armeabi-v7a", "arm64-v8a", "x86", "x86_64")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    signingConfigs {
        create("dev") {
            storeFile = keystore
            storePassword = ksStorePass
            keyAlias = ksAlias
            keyPassword = ksKeyPass
            enableV1Signing = true
            enableV2Signing = true
            enableV3Signing = true
        }
    }
    buildTypes {
        release {
            signingConfig = signingConfigs.getByName("dev")
            isMinifyEnabled = true
            isShrinkResources = true
            isDebuggable = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
    packaging {
        dex {
            // Store classes.dex uncompressed and aligned so ART can map it.
            // minSdk 24 would otherwise compress dex, which some installers
            // spend the whole "Installing..." dialog extracting.
            useLegacyPackaging = false
        }
        jniLibs {
            // Extract .so files at install time so every ABI is a normal
            // compressed library, not an uncompressed mmap that older
            // package installers reject or stall on.
            useLegacyPackaging = true
        }
    }
    sourceSets.getByName("main").assets.srcDir("../../i18n")
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(project(":protocol"))
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.compose.ui:ui:1.7.2")
    implementation("androidx.compose.material3:material3:1.3.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
    implementation("androidx.core:core-splashscreen:1.0.1")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test:rules:1.6.1")
}
