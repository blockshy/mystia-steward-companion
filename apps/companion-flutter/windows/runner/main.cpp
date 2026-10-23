#include "platform_bridge.h"
#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize COM, so that it is available for use in the library and/or
  // plugins.
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();

  // 在创建引擎与窗口之前建立单实例控制入口；端口占用不能退化成第二实例。
  const int control = StartCompanionControl(command_line_arguments);
  if (control != 0) { ::CoUninitialize(); return control == 1 ? EXIT_SUCCESS : EXIT_FAILURE; }

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  Win32Window::Point origin(10, 10);
  Win32Window::Size size(1280, 720);
  // 系统标题栏与工作台中的产品名称保持一致；透明度只作用于客户区。
  if (!window.Create(L"夜雀管家", origin, size)) {
    StopCompanionControl();
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  // v2 exit 与托盘退出通过 WM_QUIT 结束消息循环；必须在 COM 退出前销毁
  // 仍存活的 Flutter 引擎和原生桥，避免引擎持有资源导致进程无法真正退出。
  window.Destroy();
  StopCompanionControl();
  ::CoUninitialize();
  return EXIT_SUCCESS;
}
