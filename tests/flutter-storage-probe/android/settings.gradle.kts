pluginManagement {
    val lock = groovy.json.JsonSlurper().parse(file("../../../toolchain.lock.json")) as Map<*, *>
    val android = lock["flutterAndroid"] as Map<*, *>
    plugins {
        id("com.android.application") version (android["androidGradlePlugin"] as String)
        id("org.jetbrains.kotlin.android") version (android["kotlin"] as String)
    }
    val flutterSdkPath =
        run {
            val properties = java.util.Properties()
            file("local.properties").inputStream().use { properties.load(it) }
            val flutterSdkPath = properties.getProperty("flutter.sdk")
            require(flutterSdkPath != null) { "flutter.sdk not set in local.properties" }
            flutterSdkPath
        }

    includeBuild("$flutterSdkPath/packages/flutter_tools/gradle")

    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

plugins {
    id("dev.flutter.flutter-plugin-loader") version "1.0.0"
    id("com.android.application") apply false
    id("org.jetbrains.kotlin.android") apply false
}

include(":app")
