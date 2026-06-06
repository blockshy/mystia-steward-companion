declare global {
  interface Window {
    __TAURI_INTERNALS__?: unknown;
  }
}

export function isTauriRuntime() {
  return typeof window !== 'undefined' && window.__TAURI_INTERNALS__ != null;
}
