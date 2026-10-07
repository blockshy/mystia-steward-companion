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
  const HRESULT com_result =
      ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  if (FAILED(com_result)) {
    return EXIT_FAILURE;
  }

  int exit_code = EXIT_FAILURE;
  {
    // Flutter's required exit only posts WM_QUIT. Destroy the window, engine,
    // and their COM resources before uninitializing this thread's apartment.
    flutter::DartProject project(L"data");

    std::vector<std::string> command_line_arguments =
        GetCommandLineArguments();

    project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

    FlutterWindow window(project);
    Win32Window::Point origin(10, 10);
    Win32Window::Size size(800, 560);
    if (window.Create(L"mystia-steward-companion \u00b7 \u66f4\u65b0\u63a2\u9488", origin, size)) {
      window.SetQuitOnClose(true);

      ::MSG msg{};
      BOOL received;
      while ((received = ::GetMessage(&msg, nullptr, 0, 0)) > 0) {
        ::TranslateMessage(&msg);
        ::DispatchMessage(&msg);
      }
      if (received == 0) {
        exit_code = static_cast<int>(msg.wParam);
      }
    }
  }

  ::CoUninitialize();
  return exit_code;
}
