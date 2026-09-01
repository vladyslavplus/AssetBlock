'use client'

import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { subscribeProcessingHub } from '@/lib/notifications/notification-hub'
import { assetKeys } from '@/lib/catalog/asset-detail-query'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { sellerCopilotKeys } from '@/lib/seller/seller-copilot-query'
import { sellerKeys } from '@/lib/seller/seller-query'
import { sellerProcessingKeys } from '@/lib/seller/seller-processing-query'
import type { AssetProcessingUpdateMessage } from '@/lib/seller/seller-processing-schemas'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { useNotificationHubConnectionState } from '@/hooks/use-notification-hub-connection-state'
import type { HubConnectionState } from '@/lib/notifications/hub-connection-state'

function isSecurityLifecycleTerminal(msg: AssetProcessingUpdateMessage): boolean {
  if (msg.type === 'LISTING_COPILOT') {
    return false
  }
  if (msg.status === 'FAILED' || msg.status === 'CANCELLED') {
    return true
  }
  return msg.type === 'MALWARE_SCAN' && msg.status === 'SUCCEEDED'
}

/**
 * Listens to SignalR AssetProcessingUpdated events and invalidates seller, processing, and catalog keys.
 * Mount once in the authenticated shell; do not also subscribe from edit/processing panels.
 * Performs a single catch-up invalidation when transitioning from disconnected/connecting/reconnecting to connected.
 */
export function useAssetProcessingSubscription(enabled = true, userId?: string | null): void {
  const queryClient = useQueryClient()
  const hubState = useNotificationHubConnectionState()
  const prevHubStateRef = useRef<HubConnectionState>('disconnected')

  // Catch-up invalidation on transition to connected state
  useEffect(() => {
    if (!enabled || !userId) {
      prevHubStateRef.current = hubState
      return
    }

    const prevState = prevHubStateRef.current
    prevHubStateRef.current = hubState

    if (prevState !== 'connected' && hubState === 'connected') {
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerKeys.all,
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: catalogKeys.all,
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: assetKeys.all,
      })
    }
  }, [queryClient, enabled, userId, hubState])

  useEffect(() => {
    if (!enabled || !userId) {
      return
    }

    return subscribeProcessingHub((msg) => {
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerProcessingKeys.asset(msg.assetId),
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerProcessingKeys.version(msg.assetVersionId),
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerKeys.detail(msg.assetId),
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerKeys.listings(),
      })
      invalidateQueriesInBackground(queryClient, {
        queryKey: sellerKeys.versions(msg.assetId),
      })
      if (msg.type === 'LISTING_COPILOT') {
        invalidateQueriesInBackground(queryClient, {
          queryKey: sellerCopilotKeys.version(msg.assetVersionId),
        })
      }
      if (isSecurityLifecycleTerminal(msg)) {
        invalidateQueriesInBackground(queryClient, {
          queryKey: catalogKeys.all,
        })
        invalidateQueriesInBackground(queryClient, {
          queryKey: assetKeys.detail(msg.assetId),
        })
      }
    }, userId)
  }, [queryClient, enabled, userId])
}
