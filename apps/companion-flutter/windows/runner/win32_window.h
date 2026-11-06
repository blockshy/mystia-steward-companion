#ifndef RUNNER_WIN32_WINDOW_H_
#define RUNNER_WIN32_WINDOW_H_

#include <windows.h>

#include <functional>
#include <memory>
#include <string>

/// 管理支持逐显示器 DPI 的 Win32 顶层窗口、子窗口尺寸和系统消息分派。
/// 派生类通过创建、销毁及消息钩子承载渲染引擎；调用者应在所属 UI 线程操作窗口。
class Win32Window {
 public:
  struct Point {
    unsigned int x;
    unsigned int y;
    Point(unsigned int x, unsigned int y) : x(x), y(y) {}
  };

  struct Size {
    unsigned int width;
    unsigned int height;
    Size(unsigned int width, unsigned int height)
        : width(width), height(height) {}
  };

  Win32Window();
  virtual ~Win32Window();

  /// 先销毁旧窗口，再按 origin 所在的最近显示器 DPI 缩放位置和尺寸并创建新窗口。
  /// 创建时不显示窗口；只有原生窗口与 OnCreate 均成功才返回 true，显示由 Show 负责。
  bool Create(const std::wstring& title, const Point& origin, const Size& size);

  /// 使用 SW_SHOWNORMAL 显示窗口；返回值沿用 ShowWindow 的“先前是否可见”语义，
  /// 因此首次显示返回 false 并不表示显示失败。
  bool Show();

  /// 调用派生类清理钩子并释放原生窗口；OnDestroy 必须允许重复调用。
  void Destroy();

  /// 将 content 设为子窗口并填满客户区，同时将键盘焦点交给该子窗口。
  void SetChildContent(HWND content);

  /// 返回借用的顶层 HWND；窗口销毁后为 nullptr，调用者不取得句柄所有权。
  HWND GetHandle();

  /// 控制 WM_DESTROY 是否向当前 UI 线程投递退出消息。
  void SetQuitOnClose(bool quit_on_close);

  /// 返回以客户区原点为基准的物理像素范围，供子窗口布局使用。
  RECT GetClientArea();

 protected:
  /// 处理销毁、DPI、尺寸、激活与系统主题消息；未处理的消息交给 DefWindowProc。
  /// 派生类可以先行消费平台消息，剩余消息仍应转交此实现。
  virtual LRESULT MessageHandler(HWND window,
                                 UINT const message,
                                 WPARAM const wparam,
                                 LPARAM const lparam) noexcept;

  /// Create 完成原生窗口创建后调用；派生类初始化渲染资源失败时应返回 false。
  virtual bool OnCreate();

  /// Destroy 的资源清理钩子；释放派生资源后仍可安全再次调用。
  virtual void OnDestroy();

 private:
  friend class WindowClassRegistrar;

  /// WM_NCCREATE 时把实例绑定到 HWND，并尝试启用非客户区 DPI 缩放；
  /// 后续消息通过该绑定转入实例处理函数，创建前的消息保留系统默认处理。
  static LRESULT CALLBACK WndProc(HWND const window,
                                  UINT const message,
                                  WPARAM const wparam,
                                  LPARAM const lparam) noexcept;

  // 读取 WM_NCCREATE 保存的借用实例指针，不创建或延长实例生命周期。
  static Win32Window* GetThisFromHandle(HWND const window) noexcept;

  // 读取系统应用主题偏好并更新原生边框；读取失败时保留系统当前状态。
  static void UpdateTheme(HWND const window);

  bool quit_on_close_ = false;

  // 本实例管理的顶层窗口，WM_DESTROY 到达时立即清空以避免递归销毁。
  HWND window_handle_ = nullptr;

  // 承载渲染内容的借用句柄；具体内容资源由派生类管理。
  HWND child_content_ = nullptr;
};

#endif  // RUNNER_WIN32_WINDOW_H_
