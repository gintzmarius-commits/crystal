plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.liquidisland"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.liquidisland"
        minSdk = 26
        targetSdk = 34
        versionCode = 2
        versionName = "0.2"
    }

    // Cle fixe : chaque nouvel APK s'installe par-dessus le precedent
    signingConfigs {
        getByName("debug") {
            storeFile = rootProject.file("signing/crystal.keystore")
            storePassword = "crystal-public-key"
            keyAlias = "crystal"
            keyPassword = "crystal-public-key"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}
