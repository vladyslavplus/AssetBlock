'use client'

import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { subscribeProcessingHub } from '@/lib/notifications/notification-hub'
import { sellerProcessingKeys } from '@/lib/seller/seller-processing-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'

/**
 * Listens to SignalR AssetProcessingUpdated events and invalidates the exact asset and version query keys.
 */
export function useAssetProcessingSubscription(): void {
  const queryClient = useQueryClient()

  useEffect(() => {
    return subscribeProcessingHub((msg) => {
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerProcessingKeys.asset(msg.assetId),
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerProcessingKeys.version(msg.assetVersionId),
      })
    })
  }, [queryClient])
}
