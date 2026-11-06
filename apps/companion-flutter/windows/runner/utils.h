#ifndef RUNNER_UTILS_H_
#define RUNNER_UTILS_H_

#include <string>
#include <vector>

/// 为调试进程分配控制台并重新同步 C/C++ 与 Flutter 输出流；不用于正常桌面启动。
void CreateAndAttachConsole();

/// 将以零结尾的 UTF-16 参数转换为 UTF-8，读取长度限制为 UNICODE_STRING_MAX_CHARS。
/// 空指针、空输入或转换失败均返回空字符串；调用方不能将空值视为格式校验成功。
std::string Utf8FromUtf16(const wchar_t* utf16_string);

/// 使用 Windows 命令行规则拆分参数，跳过程序路径，再逐项转换为 UTF-8。
/// 拆分失败返回空列表；单项转换失败按 Utf8FromUtf16 的约定保留空项。
std::vector<std::string> GetCommandLineArguments();

#endif  // RUNNER_UTILS_H_
