import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import {
  fetchAssetProcessingJobs,
  fetchAssetVersionProcessingJobs,
} from '@/lib/seller/seller-processing-api'
import {
  isNonTerminalStatus,
  type AssetProcessingJobDto,
} from '@/lib/seller/seller-processing-schemas'

export const sellerProcessingKeys = {
  all: ['seller', 'processing'] as const,
  asset: (assetId: string) => [...sellerProcessingKeys.all, 'asset', assetId] as const,
  version: (assetVersionId: string) =>
    [...sellerProcessingKeys.all, 'version', assetVersionId] as const,
}

export const PROCESSING_POLL_INTERVAL_MS = 5000

export function useAssetProcessingJobsQuery(
  assetId: string | undefined,
  options?: { enabled?: boolean },
): UseQueryResult<AssetProcessingJobDto[], Error> {
  const enabled = (options?.enabled ?? true) && Boolean(assetId)

  return useQuery({
    queryKey: assetId ? sellerProcessingKeys.asset(assetId) : sellerProcessingKeys.all,
    queryFn: ({ signal }) => {
      if (!assetId) return Promise.resolve([])
      return fetchAssetProcessingJobs(assetId, signal)
    },
    enabled,
    refetchInterval: (query) => {
      const jobs = query.state.data
      if (!jobs || jobs.length === 0) {
        return false
      }
      const hasActive = jobs.some((j) => isNonTerminalStatus(j.status))
      return hasActive ? PROCESSING_POLL_INTERVAL_MS : false
    },
    refetchOnReconnect: true,
    refetchOnWindowFocus: true,
  })
}

export function useAssetVersionProcessingJobsQuery(
  assetVersionId: string | undefined,
  options?: { enabled?: boolean },
): UseQueryResult<AssetProcessingJobDto[], Error> {
  const enabled = (options?.enabled ?? true) && Boolean(assetVersionId)

  return useQuery({
    queryKey: assetVersionId
      ? sellerProcessingKeys.version(assetVersionId)
      : sellerProcessingKeys.all,
    queryFn: ({ signal }) => {
      if (!assetVersionId) return Promise.resolve([])
      return fetchAssetVersionProcessingJobs(assetVersionId, signal)
    },
    enabled,
    refetchInterval: (query) => {
      const jobs = query.state.data
      if (!jobs || jobs.length === 0) {
        return false
      }
      const hasActive = jobs.some((j) => isNonTerminalStatus(j.status))
      return hasActive ? PROCESSING_POLL_INTERVAL_MS : false
    },
    refetchOnReconnect: true,
    refetchOnWindowFocus: true,
  })
}
