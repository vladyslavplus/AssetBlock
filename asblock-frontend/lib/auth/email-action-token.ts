/** Read `#token=` from the URL fragment and clear it. Hash is never sent to servers. */
export function readAndClearEmailActionToken(): string | null {
  const raw = window.location.hash.startsWith('#')
    ? window.location.hash.slice(1)
    : window.location.hash
  const token = new URLSearchParams(raw).get('token')?.trim() || null
  const scrubbed = `${window.location.pathname}${window.location.search}`
  window.history.replaceState(null, '', scrubbed)
  return token
}
