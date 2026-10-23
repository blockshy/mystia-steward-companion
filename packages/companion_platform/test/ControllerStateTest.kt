package com.tyukki.mystia.steward.companion

/**
 * 无 Android/Flutter 进程的真实 Kotlin 状态测试。和产品 ControllerState.kt 一起编译，
 * 不模拟平台桥接函数、不向设备注入输入，覆盖换控制器和生命周期边界。
 */
fun main() {
    val state = ControllerState()
    var assertions = 0
    fun expect(condition: Boolean, description: String) {
        check(condition) { description }; assertions++
    }
    expect(!state.poll(true).connected, "没有手柄事件时断开")
    state.motion(1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    expect(!state.poll(true).connected, "新控制器先发断开帧")
    expect(state.poll(true).connected, "随后提供真实中立快照")
    state.motion(2, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    expect(state.poll(true).connected, "第二只空闲手柄的中立事件不能抢占归属")
    state.key(1, 0x1000, true)
    state.key(1, 0x1000, false)
    expect(state.poll(true).buttons == 0x1000L, "轮询间短按只保留一次")
    expect(state.poll(true).buttons == 0L, "短按消费后不会卡住")
    state.key(1, 0x0100, true)
    expect(state.poll(true).buttons == 0x0100L, "LB标准位")
    expect(state.poll(true).buttons == 0x0100L, "长按持续held交给Dart处理重复")
    state.key(1, 0x0100, false)
    expect(state.poll(true).buttons == 0L, "松开按键归零")
    state.motion(1, -0.8, 0.7, 0.9, 0.4, -1.0, 1.0)
    val axes = state.poll(true)
    expect(axes.leftX == -0.8 && axes.leftY == 0.7, "标准摇杆范围")
    expect(axes.leftTrigger == 0.9 && axes.rightTrigger == 0.4, "模拟扳机范围")
    expect(axes.buttons == 0x0006L, "帽方向映射到向左和向下")
    state.motion(1, Double.NaN, 4.0, Double.POSITIVE_INFINITY, -2.0, 0.0, 0.0)
    val invalid = state.poll(true)
    expect(invalid.leftX == 0.0 && invalid.leftY == 1.0, "异常轴不传播NaN或越界")
    expect(invalid.leftTrigger == 0.0 && invalid.rightTrigger == 0.0, "异常扳机归零")
    state.key(1, 0x10000, true)
    expect(state.poll(true).leftTrigger == 1.0, "数字L2同样驱动LT")
    state.key(1, 0x10000, false)
    expect(state.poll(true).leftTrigger == 0.0, "数字L2松开")
    state.key(2, 0x0200, true)
    expect(!state.poll(true).connected, "第二手柄接管必须重新回中")
    expect(state.poll(true).buttons == 0x0200L, "不继承前手柄状态")
    state.remove(1)
    expect(state.poll(true).connected, "删除非当前设备不影响当前设备")
    state.remove(2)
    expect(!state.poll(true).connected && !state.poll(true).connected, "拔出当前手柄撤销输入")
    state.key(3, 0x2000, true)
    expect(!state.poll(false).connected, "失焦立即撤销")
    expect(!state.poll(true).connected && !state.poll(true).connected, "恢复焦点不重放旧按键")
    state.key(3, 0x2000, false)
    state.poll(true)
    expect(state.poll(true).buttons == 0L, "恢复后新的松开事件建立中立态")
    state.reset()
    expect(!state.poll(true).connected, "暂停清除控制器")
    println("Android controller state: $assertions assertions passed")
}
