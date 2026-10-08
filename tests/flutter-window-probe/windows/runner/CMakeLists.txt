cmake_minimum_required(VERSION 3.14)
project(runner LANGUAGES CXX)

# Define the application target. To change its name, change BINARY_NAME in the
# top-level CMakeLists.txt, not the value here, or `flutter run` will no longer
# work.
#
# Any new source files that you add to the application should be added here.
add_executable(${BINARY_NAME} WIN32
  "flutter_window.cpp"
  "main.cpp"
  "window_api.g.cpp"
  "input_probe_api.g.cpp"
  "input_probe_bridge.cpp"
  "control_probe_api.g.cpp"
  "control_probe_bridge.cpp"
  "foreground_grant_bridge.cpp"
  "window_bridge.cpp"
  "lifecycle_controller.cpp"
  "lifecycle_primary.cpp"
  "instance_control.cpp"
  "tray_probe.cpp"
  "shell_tray_driver.cpp"
  "utils.cpp"
  "win32_window.cpp"
  "${FLUTTER_MANAGED_DIR}/generated_plugin_registrant.cc"
  "Runner.rc"
  "runner.exe.manifest"
)

# Apply the standard set of build settings. This can be removed for applications
# that need different build settings.
apply_standard_settings(${BINARY_NAME})
# This target translates C++ exceptions into Pigeon errors. Keep STL exception
# semantics enabled; the default Flutter template disables them for all targets.
get_target_property(RUNNER_DEFINITIONS ${BINARY_NAME} COMPILE_DEFINITIONS)
list(REMOVE_ITEM RUNNER_DEFINITIONS "_HAS_EXCEPTIONS=0")
set_property(TARGET ${BINARY_NAME} PROPERTY COMPILE_DEFINITIONS "${RUNNER_DEFINITIONS}")
target_compile_options(${BINARY_NAME} PRIVATE /utf-8)
# Pigeon emits an unused anonymous helper even for this single-method schema.
set_source_files_properties("window_api.g.cpp" "input_probe_api.g.cpp" "control_probe_api.g.cpp" PROPERTIES COMPILE_OPTIONS /wd4505)

# Add preprocessor definitions for the build version.
target_compile_definitions(${BINARY_NAME} PRIVATE "FLUTTER_VERSION=\"${FLUTTER_VERSION}\"")
target_compile_definitions(${BINARY_NAME} PRIVATE "FLUTTER_VERSION_MAJOR=${FLUTTER_VERSION_MAJOR}")
target_compile_definitions(${BINARY_NAME} PRIVATE "FLUTTER_VERSION_MINOR=${FLUTTER_VERSION_MINOR}")
target_compile_definitions(${BINARY_NAME} PRIVATE "FLUTTER_VERSION_PATCH=${FLUTTER_VERSION_PATCH}")
target_compile_definitions(${BINARY_NAME} PRIVATE "FLUTTER_VERSION_BUILD=${FLUTTER_VERSION_BUILD}")

# Disable Windows macros that collide with C++ standard library functions.
target_compile_definitions(${BINARY_NAME} PRIVATE "NOMINMAX")

# Add dependency libraries and include directories. Add any application-specific
# dependencies here.
target_link_libraries(${BINARY_NAME} PRIVATE flutter flutter_wrapper_app)
target_link_libraries(${BINARY_NAME} PRIVATE "dwmapi.lib" "comctl32.lib" "bcrypt.lib" "imm32.lib"
  "ws2_32.lib" "iphlpapi.lib" "shell32.lib" "ole32.lib" "oleaut32.lib" "uiautomationcore.lib" "advapi32.lib")
target_include_directories(${BINARY_NAME} PRIVATE "${CMAKE_SOURCE_DIR}")

# Run the Flutter tool portions of the build. This must not be removed.
add_dependencies(${BINARY_NAME} flutter_assemble)

set(PROBE_SHA "$ENV{MYSTIA_WINDOW_PROBE_GIT_SHA}")
string(LENGTH "${PROBE_SHA}" PROBE_SHA_LENGTH)
if(NOT PROBE_SHA MATCHES "^[0-9a-f]+$" OR NOT PROBE_SHA_LENGTH EQUAL 40)
  message(FATAL_ERROR "MYSTIA_WINDOW_PROBE_GIT_SHA must be a full lowercase Git SHA")
endif()
target_compile_definitions(${BINARY_NAME} PRIVATE "MYSTIA_WINDOW_PROBE_GIT_SHA=\"${PROBE_SHA}\"")
