import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import {
  fetchAssetProcessingJobs,
  fetchAssetVersionProcessingJobs,
} from '@/lib/seller/seller-processing-api'
import {
  isNonTerminalStatus,
  type AssetProcessingJobDto,
} from '@/lib/seller/seller-processing-schemas'
import { useNotificationHubConnectionState } from '@/hooks/use-notification-hub-connection-state'
import type { HubConnectionState } from '@/lib/notifications/hub-connection-state'

export const sellerProcessingKeys = {
  all: ['seller', 'processing'] as const,
  asset: (assetId: string) => [...sellerProcessingKeys.all, 'asset', assetId] as const,
  version: (assetVersionId: string) =>
    [...sellerProcessingKeys.all, 'version', assetVersionId] as const,
}

export const PROCESSING_POLL_INTERVAL_MS = 5000

/**
 * Pure policy function for determining processing jobs polling interval.
 * When SignalR hub is reliably connected, events drive cache invalidations, so polling is paused.
 * When disconnected, connecting, or reconnecting, active jobs fall back to HTTP polling.
 */
export function resolveProcessingPollInterval(
  jobs: AssetProcessingJobDto[] | undefined,
  hubState: HubConnectionState,
): number | false {
  if (!jobs || jobs.length === 0) {
    return false
  }
  const hasActive = jobs.some((j) => isNonTerminalStatus(j.status))
  if (!hasActive) {
    return false
  }
  if (hubState === 'connected') {
    return false
  }
  return PROCESSING_POLL_INTERVAL_MS
}

export function useAssetProcessingJobsQuery(
  assetId: string | undefined,
  options?: { enabled?: boolean },
): UseQueryResult<AssetProcessingJobDto[], Error> {
  const enabled = (options?.enabled ?? true) && Boolean(assetId)
  const hubState = useNotificationHubConnectionState()

  return useQuery({
    queryKey: assetId ? sellerProcessingKeys.asset(assetId) : sellerProcessingKeys.all,
    queryFn: ({ signal }) => {
      if (!assetId) return Promise.resolve([])
      return fetchAssetProcessingJobs(assetId, signal)
    },
    enabled,
    refetchInterval: (query) => resolveProcessingPollInterval(query.state.data, hubState),
    refetchOnReconnect: true,
    refetchOnWindowFocus: true,
  })
}

export function useAssetVersionProcessingJobsQuery(
  assetVersionId: string | undefined,
  options?: { enabled?: boolean },
): UseQueryResult<AssetProcessingJobDto[], Error> {
  const enabled = (options?.enabled ?? true) && Boolean(assetVersionId)
  const hubState = useNotificationHubConnectionState()

  return useQuery({
    queryKey: assetVersionId
      ? sellerProcessingKeys.version(assetVersionId)
      : sellerProcessingKeys.all,
    queryFn: ({ signal }) => {
      if (!assetVersionId) return Promise.resolve([])
      return fetchAssetVersionProcessingJobs(assetVersionId, signal)
    },
    enabled,
    refetchInterval: (query) => resolveProcessingPollInterval(query.state.data, hubState),
    refetchOnReconnect: true,
    refetchOnWindowFocus: true,
  })
}
