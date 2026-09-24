plugins {
    id("com.android.application")
}

android {
    namespace = "com.astralpath.app"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.astralpath.app"
        minSdk = 26
        targetSdk = 35
        versionCode = 31
        versionName = "2.1.0"
    }
    signingConfigs {
        create("release") {
            storeFile = file("../astralpath-release.keystore")
            storePassword = "astralpath2026"
            keyAlias = "astralpath"
            keyPassword = "astralpath2026"
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
