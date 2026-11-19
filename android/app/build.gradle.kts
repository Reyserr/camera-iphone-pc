plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Signature de l'APK : clé fournie par GitHub (secrets) si elle existe, sinon clé de test.
val keystorePath: String? = System.getenv("LYNXCAM_KEYSTORE")
val keystorePassword: String? = System.getenv("LYNXCAM_KEYSTORE_PASSWORD")

android {
    namespace = "com.reyserr.lynxcam"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.reyserr.lynxcam"
        minSdk = 29
        targetSdk = 35
        versionCode = 2
        versionName = "2.0"
    }

    signingConfigs {
        create("release") {
            if (keystorePath != null && keystorePassword != null) {
                storeFile = file(keystorePath)
                storePassword = keystorePassword
                keyAlias = "lynxcam"
                keyPassword = keystorePassword
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (keystorePath != null) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }
}

dependencies {
    val camerax = "1.4.1"
    implementation("androidx.camera:camera-core:$camerax")
    implementation("androidx.camera:camera-camera2:$camerax")
    implementation("androidx.camera:camera-lifecycle:$camerax")
    implementation("androidx.camera:camera-video:$camerax")
    implementation("androidx.camera:camera-view:$camerax")
    implementation("androidx.activity:activity-ktx:1.9.3")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("com.google.mlkit:segmentation-selfie:16.0.0-beta6")
}
