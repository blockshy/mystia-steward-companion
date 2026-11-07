cmake_minimum_required(VERSION 3.25)
project(mystia_portable_storage_test LANGUAGES CXX)
# 直接编译生产 Win32 存储边界，不依赖 Flutter；/W4 /WX 将编译警告作为回归失败处理。
# 用例只操作仓库内合成数据；链接系统库以验证真实句柄、DPAPI 与目录权限行为。
add_executable(portable_storage_test main.cpp ../../packages/companion_platform/windows/portable_storage.cpp)
target_compile_features(portable_storage_test PRIVATE cxx_std_17)
target_compile_definitions(portable_storage_test PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(portable_storage_test PRIVATE /utf-8 /W4 /WX /EHsc)
target_link_libraries(portable_storage_test PRIVATE bcrypt crypt32 shell32 ole32 advapi32)
