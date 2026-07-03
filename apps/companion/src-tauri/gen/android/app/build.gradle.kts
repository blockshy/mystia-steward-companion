import java.io.FileInputStream
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("rust")
}

val tauriProperties = Properties().apply {
    val propFile = file("tauri.properties")
    if (propFile.exists()) {
        propFile.inputStream().use { load(it) }
    }
}

val releaseKeystorePropertiesFile = rootProject.file("keystore.properties")
val releaseKeystoreProperties = Properties().apply {
    if (releaseKeystorePropertiesFile.exists()) {
        load(FileInputStream(releaseKeystorePropertiesFile))
    }
}
val hasReleaseKeystore = releaseKeystorePropertiesFile.exists()

fun Properties.requiredSigningProperty(name: String): String {
    return getProperty(name) ?: error("Missing Android signing property '$name' in ${releaseKeystorePropertiesFile.path}")
}

android {
    compileSdk = 36
    namespace = "com.tyukki.mystia.steward.companion"
    defaultConfig {
        applicationId = "com.tyukki.mystia.steward.companion"
        minSdk = 24
        targetSdk = 36
        versionCode = tauriProperties.getProperty("tauri.android.versionCode", "1").toInt()
        versionName = tauriProperties.getProperty("tauri.android.versionName", "1.0")
    }
    signingConfigs {
        if (hasReleaseKeystore) {
            create("release") {
                keyAlias = releaseKeystoreProperties.requiredSigningProperty("keyAlias")
                keyPassword = releaseKeystoreProperties.getProperty("keyPassword")
                    ?: releaseKeystoreProperties.requiredSigningProperty("password")
                storeFile = rootProject.file(releaseKeystoreProperties.requiredSigningProperty("storeFile"))
                storePassword = releaseKeystoreProperties.getProperty("storePassword")
                    ?: releaseKeystoreProperties.requiredSigningProperty("password")
            }
        }
    }
    buildTypes {
        getByName("debug") {
            manifestPlaceholders["usesCleartextTraffic"] = "true"
            isDebuggable = true
            isJniDebuggable = true
            isMinifyEnabled = false
            packaging {
                jniLibs.keepDebugSymbols.add("*/arm64-v8a/*.so")
                jniLibs.keepDebugSymbols.add("*/armeabi-v7a/*.so")
                jniLibs.keepDebugSymbols.add("*/x86/*.so")
                jniLibs.keepDebugSymbols.add("*/x86_64/*.so")
            }
        }
        getByName("release") {
            // The companion connects to the Mod's trusted LAN HTTP endpoint, for example
            // http://192.168.x.x:32145, so release builds must allow cleartext traffic.
            manifestPlaceholders["usesCleartextTraffic"] = "true"
            if (hasReleaseKeystore) {
                signingConfig = signingConfigs.getByName("release")
            }
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                *fileTree(".") { include("**/*.pro") }
                    .plus(getDefaultProguardFile("proguard-android-optimize.txt"))
                    .toList().toTypedArray()
            )
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        buildConfig = true
    }
}

rust {
    rootDirRel = "../../../"
}

dependencies {
    implementation("androidx.webkit:webkit:1.14.0")
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.activity:activity-ktx:1.10.1")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.lifecycle:lifecycle-process:2.10.0")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.1.4")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.5.0")
}

apply(from = "tauri.build.gradle.kts")
