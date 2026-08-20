import type { InvalidateQueryFilters, QueryClient } from '@tanstack/react-query'

function isAbortError(error: unknown): boolean {
  return (
    (error instanceof DOMException && error.name === 'AbortError') ||
    (typeof error === 'object' && error !== null && 'name' in error && error.name === 'AbortError')
  )
}

export function runQueryInBackground(task: Promise<unknown>): void {
  void task.catch((error: unknown) => {
    if (!isAbortError(error)) {
      console.error('Background query refresh failed.', error)
    }
  })
}

export function invalidateQueriesInBackground(
  queryClient: QueryClient,
  filters: InvalidateQueryFilters,
): void {
  runQueryInBackground(queryClient.invalidateQueries(filters, { cancelRefetch: false }))
}
