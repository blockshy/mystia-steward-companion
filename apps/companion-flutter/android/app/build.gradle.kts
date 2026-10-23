import java.util.Properties
import java.io.File
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("dev.flutter.flutter-gradle-plugin")
}
// 既有正式签名配置可通过仅本进程环境显式指定，避免为本地构建复制密码文件。
// CI 仍默认读取临时物化的 keystore.properties；路径本身不是密钥，文件内容绝不进入日志。
val signingConfigPath = providers.environmentVariable("MYSTIA_ANDROID_KEYSTORE_PROPERTIES").orNull
require(signingConfigPath == null || File(signingConfigPath).isAbsolute) {
    "MYSTIA_ANDROID_KEYSTORE_PROPERTIES must be an absolute path."
}
val releaseKeystorePropertiesFile = rootProject.file(signingConfigPath ?: "keystore.properties")
val signingProperties = Properties().apply {
    if (releaseKeystorePropertiesFile.exists()) {
        require(releaseKeystorePropertiesFile.isFile && releaseKeystorePropertiesFile.canRead()) {
            "Android signing configuration is not a readable regular file."
        }
        releaseKeystorePropertiesFile.inputStream().use { load(it) }
        val requiredKeys = setOf("keyAlias", "storePassword", "keyPassword", "storeFile")
        require(stringPropertyNames() == requiredKeys) { "Unsupported Android signing properties; expected four canonical keys." }
        require(requiredKeys.all { !getProperty(it).isNullOrBlank() }) { "Android signing properties must not be empty." }
    }
}
android {
    namespace = "com.tyukki.mystia.steward.companion"
    compileSdk = 36
    ndkVersion = "30.0.14904198"
    buildToolsVersion = "35.0.0"
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    // 只纳入 Pigeon 输出，避免 Flutter 工具产生的 package/android/app 注册器混入产品。
    sourceSets.getByName("main").java.srcDir("../../../../packages/companion_platform/android/src")
    defaultConfig {
        applicationId = "com.tyukki.mystia.steward.companion"
        minSdk = 24
        targetSdk = 36
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }
    signingConfigs {
        if (releaseKeystorePropertiesFile.exists()) {
            create("release") {
                keyAlias = signingProperties.getProperty("keyAlias")
                keyPassword = signingProperties.getProperty("keyPassword")
                storePassword = signingProperties.getProperty("storePassword")
                storeFile = rootProject.file(signingProperties.getProperty("storeFile"))
            }
        }
    }
    buildTypes {
        release {
            // 正式构建只允许既有正式证书，不回退到 debug；缺配置由下方任务在编译前明确拒绝。
            if (releaseKeystorePropertiesFile.exists()) signingConfig = signingConfigs.getByName("release")
        }
    }
}
// Android 安装器不会接受 unsigned APK。无签名的“release.apk”不能再静默成为看似可安装的输出。
// 将门禁挂在所有 Release 编译之前，debug/IDE 模型查询仍可独立运行。
val verifyReleaseSigning = tasks.register("verifyReleaseSigning") {
    doLast {
        require(releaseKeystorePropertiesFile.isFile && releaseKeystorePropertiesFile.canRead()) {
            "Release APK requires the existing signing configuration. Set MYSTIA_ANDROID_KEYSTORE_PROPERTIES or provide android/keystore.properties; unsigned APKs cannot be installed."
        }
        require(rootProject.file(signingProperties.getProperty("storeFile")).isFile) {
            "Release keystore does not exist. Do not create a replacement certificate for the existing applicationId."
        }
    }
}
tasks.configureEach {
    if (name == "preReleaseBuild") dependsOn(verifyReleaseSigning)
}
flutter { source = "../.." }
