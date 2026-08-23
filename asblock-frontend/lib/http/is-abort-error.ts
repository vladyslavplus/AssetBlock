/**
 * True when a failure is (or should be treated as) fetch/query cancellation.
 * Checks DOMException and Error name for environments where AbortError is either.
 */
export function isAbortError(error: unknown, signal?: AbortSignal | null): boolean {
  if (signal?.aborted) return true
  if (
    typeof DOMException !== 'undefined' &&
    error instanceof DOMException &&
    error.name === 'AbortError'
  ) {
    return true
  }
  if (error instanceof Error && error.name === 'AbortError') return true
  return false
}
