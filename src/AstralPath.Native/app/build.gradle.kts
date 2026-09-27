plugins {
    id("com.android.application")
}

android {
    namespace = "com.astralpath.app"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.astralpath.app.v22"
        minSdk = 26
        targetSdk = 35
        versionCode = 32
        versionName = "2.2.0"
    }
    signingConfigs {
        // P3-16 密钥轮换（2026-09-24）：口令/路径一律环境变量；仓库内不存密钥
        // 本地/CI 先 load ~/.android/astralpath-signing/signing.env
        create("release") {
            val ks = System.getenv("ASTRALPATH_KEYSTORE")
                ?: (System.getProperty("user.home") + "/.android/astralpath-signing/astralpath-release-2026.keystore")
            storeFile = file(ks)
            storePassword = System.getenv("ASTRALPATH_STORE_PASSWORD") ?: ""
            keyAlias = System.getenv("ASTRALPATH_KEY_ALIAS") ?: "astralpath"
            keyPassword = System.getenv("ASTRALPATH_KEY_PASSWORD") ?: ""
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlin {
        compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) }
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.webkit:webkit:1.12.1")
}


