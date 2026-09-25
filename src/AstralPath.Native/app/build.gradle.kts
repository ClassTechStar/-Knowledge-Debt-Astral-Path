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
        // 口令一律来自环境变量/CI secret，禁止写入仓库
        create("release") {
            val ks = System.getenv("ASTRALPATH_KEYSTORE") ?: "../astralpath-release.keystore"
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


