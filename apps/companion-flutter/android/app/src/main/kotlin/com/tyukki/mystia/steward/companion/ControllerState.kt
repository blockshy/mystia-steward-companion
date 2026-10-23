package com.tyukki.mystia.steward.companion

/**
 * Android 手柄的纯状态层，输出与 Windows XInput 相同的标准位掩码和轴范围。
 * Activity 只负责把平台事件转换成数据；本类不引用 Android SDK，可用 JVM 离线验证
 * 拔插、换手柄、失焦和短按。所有调用归属 Activity/UI 线程，不保存跨线程对象。
 */
internal class ControllerState {
    data class Snapshot(val connected: Boolean, val buttons: Long = 0,
        val leftX: Double = 0.0, val leftY: Double = 0.0,
        val leftTrigger: Double = 0.0, val rightTrigger: Double = 0.0)

    private data class Input(var buttons: Long = 0, var latched: Long = 0,
        var hats: Long = 0, var x: Double = 0.0, var y: Double = 0.0,
        var left: Double = 0.0, var right: Double = 0.0)
    private val devices = mutableMapOf<Int, Input>()
    private var owner: Int? = null
    private var ownerChanged = false

    /** 仅明确的新设备活动能接管输入；切换时发送一个断开帧，Dart 会重新等待回中。 */
    private fun input(id: Int, active: Boolean): Input {
        val current = devices.getOrPut(id) { Input() }
        // 某些驱动会周期发送中立轴；第二只空闲手柄不能持续抢走当前操作归属。
        if (owner != id && (owner == null || active)) { owner = id; ownerChanged = true }
        return current
    }
    fun key(id: Int, mask: Long, pressed: Boolean) {
        val value = input(id, pressed)
        if (pressed) {
            value.buttons = value.buttons or mask
            // 保留两个40ms轮询之间的短按；消费一次后清空，长按仍由真实 held 决定。
            value.latched = value.latched or mask
        } else value.buttons = value.buttons and mask.inv()
    }
    fun motion(id: Int, x: Double, y: Double, left: Double, right: Double,
               hatX: Double, hatY: Double) {
        val value = input(id, kotlin.math.abs(x) > 0.25 || kotlin.math.abs(y) > 0.25 ||
            left > 0.25 || right > 0.25 || kotlin.math.abs(hatX) > 0.5 || kotlin.math.abs(hatY) > 0.5)
        value.x = finite(x, -1.0, 1.0); value.y = finite(y, -1.0, 1.0)
        value.left = finite(left, 0.0, 1.0); value.right = finite(right, 0.0, 1.0)
        value.hats = (if (hatX < -0.5) 0x0004L else if (hatX > 0.5) 0x0008L else 0L) or
            (if (hatY < -0.5) 0x0001L else if (hatY > 0.5) 0x0002L else 0L)
    }
    private fun finite(value: Double, low: Double, high: Double) =
        if (value.isFinite()) value.coerceIn(low, high) else 0.0
    fun remove(id: Int) {
        devices.remove(id)
        if (owner == id) { owner = null; ownerChanged = true }
    }
    /** 焦点/生命周期边界撤销旧 held，恢复后必须以真实新事件重新选择设备。 */
    fun reset() { devices.clear(); owner = null; ownerChanged = true }
    fun poll(focused: Boolean): Snapshot {
        if (!focused) { reset(); return Snapshot(false) }
        if (ownerChanged) { ownerChanged = false; return Snapshot(false) }
        val active = owner ?: return Snapshot(false)
        val value = devices[active] ?: return Snapshot(false)
        val buttons = value.buttons or value.latched or value.hats
        value.latched = 0
        return Snapshot(true, buttons, value.x, value.y,
            maxOf(value.left, if (buttons and 0x10000L != 0L) 1.0 else 0.0),
            maxOf(value.right, if (buttons and 0x20000L != 0L) 1.0 else 0.0))
    }
}
