export function isLoopbackLocalApiEndpoint(endpoint: string): boolean {
  try {
    const url = new URL(endpoint);
    if (url.protocol !== 'http:') return false;
    const hostname = url.hostname.toLowerCase();
    return hostname === '127.0.0.1'
      || hostname === 'localhost';
  } catch {
    return false;
  }
}
