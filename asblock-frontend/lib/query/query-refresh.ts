import type { InvalidateQueryFilters, QueryClient } from '@tanstack/react-query'
import { isAbortError } from '@/lib/http/is-abort-error'

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
