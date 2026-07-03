const CLIENT_ID_STORAGE_KEY = 'mystia-steward-companion-client-id';

export function readCompanionClientId(): string {
  const existing = localStorage.getItem(CLIENT_ID_STORAGE_KEY);
  if (existing && isValidClientId(existing)) return existing;

  const next = createClientId();
  localStorage.setItem(CLIENT_ID_STORAGE_KEY, next);
  return next;
}

export function readCompanionClientLabel(): string {
  const userAgent = navigator.userAgent.toLowerCase();
  if (/android/.test(userAgent)) return 'Android companion';
  if (/windows/.test(userAgent)) return 'Windows companion';
  return 'Companion';
}

function createClientId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  const bytes = new Uint8Array(16);
  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
    crypto.getRandomValues(bytes);
  } else {
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Math.floor(Math.random() * 256);
    }
  }

  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

function isValidClientId(value: string): boolean {
  return /^[a-zA-Z0-9-]{16,64}$/.test(value);
}
