const PROJECT_RELEASES_ORIGIN = 'https://github.com';
const PROJECT_RELEASES_PATH = '/blockshy/mystia-steward-companion/releases';

export function normalizeProjectReleaseUrl(url: string): string {
  const trimmed = url.trim();
  if (!trimmed || trimmed.includes('\0')) throw new Error('发布页地址为空或格式无效。');

  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    throw new Error('发布页地址格式无效。');
  }

  const isProjectRelease = parsed.origin === PROJECT_RELEASES_ORIGIN
    && (parsed.pathname === PROJECT_RELEASES_PATH || parsed.pathname.startsWith(`${PROJECT_RELEASES_PATH}/`));
  if (!isProjectRelease || parsed.username || parsed.password) {
    throw new Error('只允许打开本项目的 GitHub Release 页面。');
  }

  return parsed.toString();
}
