import groovy.json.JsonSlurper
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("dev.flutter.flutter-gradle-plugin")
}
val lock = JsonSlurper().parse(rootProject.file("../../../toolchain.lock.json")) as Map<*, *>
val androidLock = lock["android"] as Map<*, *>
android {
    namespace = "com.tyukki.mystia.steward.companion.storagep0"
    compileSdk = (androidLock["compileSdk"] as Number).toInt()
    buildToolsVersion = androidLock["buildTools"] as String
    ndkVersion = androidLock["ndkPackage"] as String
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    defaultConfig {
        applicationId = "com.tyukki.mystia.steward.companion.storagep0"
        minSdk = 24
        targetSdk = (androidLock["targetSdk"] as Number).toInt()
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }
    buildTypes {
        release {
            // Isolated test identity only; never uses the product signing key.
            signingConfig = signingConfigs.getByName("debug")
        }
    }
    packaging { jniLibs { useLegacyPackaging = false } }
}
kotlin { compilerOptions { jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17 } }
flutter { source = "../.." }
