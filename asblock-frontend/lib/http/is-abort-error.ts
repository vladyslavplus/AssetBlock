/**
 * True when a failure is (or should be treated as) fetch/query cancellation.
 * Checks DOMException and Error name for environments where AbortError is either.
 * If the provided signal is already aborted, the failure is treated as cancellation
 * even when the runtime surfaces it as a generic TypeError.
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

/** Normalize cancellation to AbortError without classifying unrelated network failures. */
export function toAbortError(error: unknown, signal?: AbortSignal | null): Error {
  if (
    typeof DOMException !== 'undefined' &&
    error instanceof DOMException &&
    error.name === 'AbortError'
  ) {
    return error
  }
  if (error instanceof Error && error.name === 'AbortError') {
    return error
  }
  if (typeof DOMException !== 'undefined') {
    return new DOMException(
      signal?.aborted ? 'The operation was aborted.' : 'The operation was aborted.',
      'AbortError',
    )
  }
  return Object.assign(new Error('The operation was aborted.'), { name: 'AbortError' })
}

/**
 * Attach a catch so a sibling abort in Promise.all is not an unhandled rejection,
 * then rethrow the normalized AbortError.
 */
export function keepAbortable<T>(promise: Promise<T>, signal?: AbortSignal | null): Promise<T> {
  return promise.catch((error: unknown) => {
    if (isAbortError(error, signal)) {
      throw toAbortError(error, signal)
    }
    throw error
  })
}
