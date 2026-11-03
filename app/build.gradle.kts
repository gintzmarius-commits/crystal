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
        // Numero de build GitHub : chaque version publiee est plus grande que la precedente
        val run = System.getenv("GITHUB_RUN_NUMBER")?.toIntOrNull() ?: 0
        versionCode = 100 + run
        versionName = "1.0.$run"
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

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}
