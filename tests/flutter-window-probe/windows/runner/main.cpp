#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include "flutter_window.h"
#include "utils.h"
#include "window_bridge.h"
#include "instance_control.h"
#include "lifecycle_controller.h"
#include "input_probe_bridge.h"
#include "control_probe_bridge.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  const auto early_arguments = GetCommandLineArguments();
  // No GUI/COM/fixture is created. CI uses the packaged EXE to catch loader
  // dependencies which a successful linker invocation cannot establish.
  if (early_arguments == std::vector<std::string>{"--loader-check", MYSTIA_WINDOW_PROBE_GIT_SHA}) {
    return EXIT_SUCCESS;
  }
  if (!early_arguments.empty() && early_arguments[0] == "--target") {
    return RunWindowProbeTarget(early_arguments);
  }
  if (IsControlLifecycleController(early_arguments)) return RunControlLifecycleController(early_arguments);
  const bool control_client = ValidateControlClientInvocation(early_arguments);
  std::unique_ptr<lifecycle_probe::ChildContext> child_context;
  if (!early_arguments.empty() && (early_arguments[0] == "--lifecycle-primary" || early_arguments[0] == "--lifecycle-peer")) {
    try {
      child_context = std::make_unique<lifecycle_probe::ChildContext>(early_arguments);
      if (early_arguments[0] == "--lifecycle-peer") {
        return lifecycle_probe::RunInstancePeer(child_context->shared(), child_context->action());
      }
    } catch (const std::exception&) { return EXIT_FAILURE; }
  } else if (!ValidateWindowProbeInvocation(early_arguments) &&
             !ValidateInputProbeInvocation(early_arguments) &&
             !ValidateControlProbeInvocation(early_arguments) && !control_client) {
      return EXIT_FAILURE;
  }
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
    if (control_client) command_line_arguments = ControlClientDartArguments(command_line_arguments);

    project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

    FlutterWindow window(project, child_context ? &child_context->shared() : nullptr);
    Win32Window::Point origin(10, 10);
    Win32Window::Size size(800, 560);
    if (window.Create(L"mystia-steward-companion \u00b7 \u7a97\u53e3\u63a2\u9488", origin, size)) {
      window.SetQuitOnClose(true);

      ::MSG msg{};
      BOOL received;
      while ((received = ::GetMessage(&msg, nullptr, 0, 0)) > 0) {
        ObserveWindowProbeQueuedMessage(msg);
        LifecyclePrimary::ObserveQueuedMessage(msg);
        InputProbeBridge::ObserveQueuedMessage(msg);
        ControlProbeBridge::ObserveQueuedMessage(msg);
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
