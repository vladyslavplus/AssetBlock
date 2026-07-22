'use client'

import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'

import { libraryKeys } from '@/lib/library/library-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'

/**
 * Ensures /library TanStack cache is stale after returning from payment so new purchases appear immediately.
 */
export function InvalidateLibraryAfterCheckout() {
  const queryClient = useQueryClient()
  const ran = useRef(false)

  useEffect(() => {
    if (ran.current) {
      return
    }
    ran.current = true
    invalidateQueriesInBackground(queryClient, { queryKey: libraryKeys.purchases() })
  }, [queryClient])

  return null
}
