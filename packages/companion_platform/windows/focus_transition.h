#pragma once
#include <cstdint>

/// 跨输入队列的窗口激活是异步的：系统接受请求时，前台 HWND 可能尚未改变。
/// 此对象只决定何时隐藏伴随窗口，不接管系统焦点，也不阻塞任何窗口的消息循环。
/// 所有方法由原生 UI 线程调用；时间参数来自同一个单调时钟。
class FocusTransition final {
 public:
  /// 接受新的切换后开始等待观察结果；失败请求不能留下上一次的隐藏意图。
  bool Begin(bool accepted, uint64_t now) {
    pending_ = accepted;
    started_ = now;
    return accepted;
  }

  void Cancel() { pending_ = false; }

  /// 仅在目标进程确实成为前台后返回一次 true。用户切到第三个窗口、
  /// 游戏退出或目标长时间没有响应时放弃隐藏，避免迟到的切换吞掉当前界面。
  bool Observe(uint64_t now, bool target_alive, bool target_foreground,
               bool source_foreground) {
    if (!pending_) return false;
    if (!target_alive || now - started_ >= 1500) {
      Cancel();
      return false;
    }
    if (target_foreground) {
      Cancel();
      return true;
    }
    if (!source_foreground) Cancel();
    return false;
  }

 private:
  bool pending_ = false;
  uint64_t started_ = 0;
};
