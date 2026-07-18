import { isTauriRuntime } from '@/lib/tauri-runtime';
import { normalizeProjectReleaseUrl } from '@/lib/project-release-url';

/**
 * 打开项目 Release 页面。
 *
 * Tauri 打包后的 WebView 对 `window.open` 的行为不稳定，可能直接吞掉新窗口请求；
 * 因此打包运行时统一交给 Tauri opener 调用系统默认浏览器。浏览器开发模式保留
 * `window.open`，便于本地预览和 UI 巡检。
 */
export async function openProjectReleaseUrl(url: string): Promise<void> {
  const normalizedUrl = normalizeProjectReleaseUrl(url);

  if (isTauriRuntime()) {
    const { openUrl } = await import('@tauri-apps/plugin-opener');
    await openUrl(normalizedUrl);
    return;
  }

  const opened = window.open(normalizedUrl, '_blank', 'noopener,noreferrer');
  if (!opened) {
    window.location.assign(normalizedUrl);
  }
}

export { normalizeProjectReleaseUrl } from '@/lib/project-release-url';
