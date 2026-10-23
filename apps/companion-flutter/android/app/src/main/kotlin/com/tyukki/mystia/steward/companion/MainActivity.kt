package com.tyukki.mystia.steward.companion

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import android.content.Intent
import android.hardware.input.InputManager
import android.net.Uri
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 原生安全存储和平台能力。后台租约由应用生命周期协调，不在 Activity 中常驻续约。
 * 密文损坏、密钥失效和写入失败均保留原文件并报告错误，不自动重置设备身份。
 */
class MainActivity : FlutterActivity(), PlatformHostApi, InputManager.InputDeviceListener {
    private val controller = ControllerState()
    private val inputManager: InputManager get() = getSystemService(INPUT_SERVICE) as InputManager

    override fun onResume() {
        super.onResume()
        inputManager.registerInputDeviceListener(this, null)
    }
    override fun onPause() {
        controller.reset()
        inputManager.unregisterInputDeviceListener(this)
        super.onPause()
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        if (!hasFocus) controller.reset()
        super.onWindowFocusChanged(hasFocus)
    }
    override fun onInputDeviceAdded(deviceId: Int) = Unit
    override fun onInputDeviceRemoved(deviceId: Int) { controller.remove(deviceId) }
    override fun onInputDeviceChanged(deviceId: Int) { controller.remove(deviceId) }

    /** 不吞掉手机返回键或键盘方向键；只有明确来自游戏手柄的标准按钮进入导航。 */
    private fun isController(device: InputDevice?) = device != null &&
        (device.supportsSource(InputDevice.SOURCE_GAMEPAD) || device.supportsSource(InputDevice.SOURCE_JOYSTICK))
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (hasWindowFocus() && isController(event.device)) {
            val mask = when (event.keyCode) {
                KeyEvent.KEYCODE_DPAD_UP -> 0x0001L
                KeyEvent.KEYCODE_DPAD_DOWN -> 0x0002L
                KeyEvent.KEYCODE_DPAD_LEFT -> 0x0004L
                KeyEvent.KEYCODE_DPAD_RIGHT -> 0x0008L
                KeyEvent.KEYCODE_BUTTON_A -> 0x1000L
                KeyEvent.KEYCODE_BUTTON_B -> 0x2000L
                KeyEvent.KEYCODE_BUTTON_X -> 0x4000L
                KeyEvent.KEYCODE_BUTTON_Y -> 0x8000L
                KeyEvent.KEYCODE_BUTTON_L1 -> 0x0100L
                KeyEvent.KEYCODE_BUTTON_R1 -> 0x0200L
                KeyEvent.KEYCODE_BUTTON_L2 -> 0x10000L
                KeyEvent.KEYCODE_BUTTON_R2 -> 0x20000L
                else -> 0L
            }
            if (mask != 0L && (event.action == KeyEvent.ACTION_DOWN || event.action == KeyEvent.ACTION_UP)) {
                if (event.repeatCount == 0) controller.key(event.deviceId, mask, event.action == KeyEvent.ACTION_DOWN)
                return true
            }
        }
        return super.dispatchKeyEvent(event)
    }
    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        if (!hasWindowFocus() || !isController(event.device) || event.action != MotionEvent.ACTION_MOVE ||
            (event.source and InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK) return super.onGenericMotionEvent(event)
        val device = event.device ?: return super.onGenericMotionEvent(event)
        fun axis(axis: Int, trigger: Boolean = false): Double {
            val range = device.getMotionRange(axis, event.source) ?: device.getMotionRange(axis) ?: return 0.0
            val raw = event.getAxisValue(axis)
            if (!raw.isFinite()) return 0.0
            if (trigger) return if (range.max > range.min)
                ((raw - range.min) / (range.max - range.min)).toDouble().coerceIn(0.0, 1.0) else 0.0
            return if (kotlin.math.abs(raw) <= range.flat) 0.0 else raw.toDouble().coerceIn(-1.0, 1.0)
        }
        // Android 的Y向下为正，XInput/Dart契约向上为正，因此在单一适配层翻转。
        controller.motion(event.deviceId, axis(MotionEvent.AXIS_X), -axis(MotionEvent.AXIS_Y),
            maxOf(axis(MotionEvent.AXIS_LTRIGGER, true), axis(MotionEvent.AXIS_BRAKE, true)),
            maxOf(axis(MotionEvent.AXIS_RTRIGGER, true), axis(MotionEvent.AXIS_GAS, true)),
            axis(MotionEvent.AXIS_HAT_X), axis(MotionEvent.AXIS_HAT_Y))
        return true
    }
    private val alias = "mystia-companion-flutter-v1"
    // Android 提供的 /data/user/0 可能本身是系统别名；先规范化可信基目录，再检查应用自建路径。
    private val root: File get() = File(noBackupFilesDir.canonicalFile, "companion-v1").also {
        check(it.exists() || it.mkdirs()) { "应用存储目录不可用。" }
        check(it.canonicalFile == it.absoluteFile) { "不允许重定向应用存储目录。" }
    }
    private fun file(name: String): File = File(root, name).also {
        check(it.canonicalFile == it.absoluteFile && it.canonicalFile.parentFile == root) { "存储路径超出范围或被重定向。" }
    }
    private fun validateAtomicPaths(file: File) {
        // AtomicFile 的备份/待提交路径同样不能成为符号链接，检查失败时保留现状。
        for (suffix in arrayOf("", ".bak", ".new")) {
            val candidate = File(file.path + suffix)
            check(candidate.canonicalFile == candidate.absoluteFile) { "原子存储路径被重定向。" }
            check(!candidate.exists() || candidate.isFile) { "原子存储路径不是普通文件。" }
        }
    }
    private fun keyFile(key: String): File {
        require(Regex("[a-z0-9-]{1,64}").matches(key)) { "无效的安全存储键。" }
        return file("$key.aesgcm")
    }
    private fun secretKey(create: Boolean): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(alias, null) as? SecretKey)?.let { return it }
        check(create) { "历史密文的安全密钥不可用，原文件已保留。" }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true).build())
        }.generateKey()
    }
    private fun read(file: File): ByteArray? {
        validateAtomicPaths(file)
        if (!file.exists() && !File(file.path + ".bak").exists()) return null
        AtomicFile(file).openRead().use { input ->
            // minSdk 24 不使用依赖较新 Android 的 readNBytes。
            val output = java.io.ByteArrayOutputStream()
            val buffer = ByteArray(8192)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                check(output.size() + count <= 1024 * 1024) { "持久化文件超过容量限制。" }
                output.write(buffer, 0, count)
            }
            return output.toByteArray()
        }
    }
    private fun write(file: File, bytes: ByteArray) {
        require(bytes.size <= 1024 * 1024) { "持久化内容过大。" }
        validateAtomicPaths(file)
        val atomic = AtomicFile(file)
        val stream = atomic.startWrite()
        try { stream.write(bytes); stream.fd.sync(); atomic.finishWrite(stream) }
        catch (error: Exception) { atomic.failWrite(stream); throw error }
        check(read(file)?.contentEquals(bytes) == true) { "持久化回读不一致，不能确认同步。" }
    }
    override fun configureFlutterEngine(engine: FlutterEngine) {
        super.configureFlutterEngine(engine)
        PlatformHostApi.setUp(engine.dartExecutor.binaryMessenger, this)
    }
    override fun cleanUpFlutterEngine(engine: FlutterEngine) {
        PlatformHostApi.setUp(engine.dartExecutor.binaryMessenger, null)
        super.cleanUpFlutterEngine(engine)
    }
    override fun initialize() = PlatformLaunch(desktop = false)
    override fun applyWindowPreferences(preferences: WindowPreferences) {
        require(!preferences.mousePassthrough) { "Android 不支持鼠标穿透。" }
    }
    override fun performWindowAction(action: WindowAction): Boolean = when (action) {
        WindowAction.EXIT -> { finishAndRemoveTask(); true }
        WindowAction.SHOW -> hasWindowFocus()
        else -> false
    }
    override fun pollInput(): PlatformInput {
        val focused = hasWindowFocus()
        val input = controller.poll(focused)
        return PlatformInput(focused, input.connected, input.buttons, input.leftX, input.leftY,
            input.leftTrigger, input.rightTrigger, 0, initialize())
    }
    override fun openExternalUrl(url: String): Boolean {
        // 仅本项目发布页可离开客户端；不能通过Pigeon绕过Dart的URL白名单。
        require(Regex("https://github\\.com/blockshy/mystia-steward-companion/releases(?:/latest|/tag/[A-Za-z0-9._-]+)?").matches(url)) {
            "只允许打开夜雀管家的 GitHub 发布页面。"
        }
        return try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))); true }
        catch (_: android.content.ActivityNotFoundException) { false }
    }
    override fun readSecret(key: String): String? {
        val bytes = read(keyFile(key)) ?: return null
        require(bytes.size in 30..65536 && bytes[0].toInt() == 1) { "安全存储格式损坏，原文件已保留。" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, secretKey(false), GCMParameterSpec(128, bytes.copyOfRange(1, 13)))
        cipher.updateAAD(key.toByteArray(Charsets.UTF_8))
        val plain = cipher.doFinal(bytes.copyOfRange(13, bytes.size))
        return try { plain.toString(Charsets.UTF_8) } finally { plain.fill(0) }
    }
    override fun writeSecret(key: String, value: String) {
        require(value.toByteArray(Charsets.UTF_8).size in 1..60000) { "安全存储内容长度无效。" }
        readSecret(key)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, secretKey(true))
        cipher.updateAAD(key.toByteArray(Charsets.UTF_8))
        val plain = value.toByteArray(Charsets.UTF_8)
        try { write(keyFile(key), byteArrayOf(1) + cipher.iv + cipher.doFinal(plain)) }
        finally { plain.fill(0) }
        check(readSecret(key) == value) { "安全存储回读不一致。" }
    }
    override fun deleteSecret(key: String) {
        if (readSecret(key) != null) check(keyFile(key).delete()) { "安全存储删除失败。" }
    }
    override fun readPreferences(): String? = read(file("preferences.json"))?.toString(Charsets.UTF_8)
    override fun writePreferences(json: String) = write(file("preferences.json"), json.toByteArray(Charsets.UTF_8))
}
