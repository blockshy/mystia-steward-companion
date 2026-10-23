#include "../windows/focus_transition.h"
#include <cstdlib>
#include <iostream>

/// 不创建窗口、不抢占桌面焦点；以系统实际允许的跨队列时序回归隐藏决策。
/// 失败使用 abort，避免 Release 编译关闭 assert 后出现虚假的通过结果。
static int checks = 0;
static void Check(bool value) { ++checks; if (!value) std::abort(); }

int main() {
  FocusTransition transition;
  Check(transition.Begin(true, 100));
  Check(!transition.Observe(101, true, false, true));
  Check(!transition.Observe(140, true, false, true));
  Check(transition.Observe(180, true, true, false));
  Check(!transition.Observe(220, true, true, false));

  // 系统拒绝、进程退出、超时和用户切到第三方窗口都不得隐藏伴随窗口。
  Check(!transition.Begin(false, 300));
  Check(!transition.Observe(340, true, true, false));
  Check(transition.Begin(true, 400));
  Check(!transition.Observe(440, false, true, false));
  Check(transition.Begin(true, 500));
  Check(!transition.Observe(2000, true, true, false));
  Check(transition.Begin(true, 2100));
  Check(!transition.Observe(2140, true, false, false));
  Check(!transition.Observe(2180, true, true, false));

  // 用户主动恢复管家会取消未完成的隐藏请求；之后仍可正常开始下一次切换。
  Check(transition.Begin(true, 3000));
  transition.Cancel();
  Check(!transition.Observe(3040, true, true, false));
  Check(transition.Begin(true, 4000));
  Check(transition.Observe(4001, true, true, false));
  std::cout << "PASS: asynchronous focus transition (" << checks << " checks)\n";
}
