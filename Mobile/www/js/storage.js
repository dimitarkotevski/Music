// Settings live on the device; storage can be unavailable (private mode), so every access is guarded.
const PREFIX = 'tuner.';

export function load(key, fallback) {
  try {
    const value = localStorage.getItem(PREFIX + key);
    return value === null ? fallback : JSON.parse(value);
  } catch {
    return fallback;
  }
}

export function save(key, value) {
  try {
    localStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch { /* not saved */ }
}
