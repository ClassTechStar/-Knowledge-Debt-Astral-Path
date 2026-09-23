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
        versionCode = 15
        versionName = "1.4.0"
    }
    buildTypes {
        release { isMinifyEnabled = false }
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
