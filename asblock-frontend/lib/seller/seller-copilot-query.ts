import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query'

import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import {
  enqueueListingCopilot,
  fetchListingCopilotSuggestion,
  ListingCopilotRequestError,
} from '@/lib/seller/seller-copilot-api'
import type {
  ListingCopilotEnqueueResponse,
  ListingCopilotSuggestion,
} from '@/lib/seller/seller-copilot-schemas'
import { sellerProcessingKeys } from '@/lib/seller/seller-processing-query'

export const sellerCopilotKeys = {
  all: ['seller', 'copilot'] as const,
  version: (assetVersionId: string) =>
    [...sellerCopilotKeys.all, 'version', assetVersionId] as const,
}

export function useListingCopilotSuggestionQuery(
  assetVersionId: string | undefined,
  options?: { enabled?: boolean },
): UseQueryResult<ListingCopilotSuggestion | null, Error> {
  const enabled = (options?.enabled ?? true) && Boolean(assetVersionId)

  return useQuery({
    queryKey: assetVersionId ? sellerCopilotKeys.version(assetVersionId) : sellerCopilotKeys.all,
    queryFn: () => {
      if (!assetVersionId) {
        return Promise.resolve(null)
      }
      return fetchListingCopilotSuggestion(assetVersionId)
    },
    enabled,
  })
}

export function useEnqueueListingCopilotMutation(
  assetId: string | undefined,
  assetVersionId: string | undefined,
): UseMutationResult<ListingCopilotEnqueueResponse, ListingCopilotRequestError, void> {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: () => {
      if (!assetVersionId) {
        return Promise.reject(new ListingCopilotRequestError('Missing version.', 400))
      }
      return enqueueListingCopilot(assetVersionId)
    },
    onSuccess: () => {
      if (assetVersionId) {
        invalidateQueriesInBackground(queryClient, {
          queryKey: sellerCopilotKeys.version(assetVersionId),
        })
        invalidateQueriesInBackground(queryClient, {
          queryKey: sellerProcessingKeys.version(assetVersionId),
        })
      }
      if (assetId) {
        invalidateQueriesInBackground(queryClient, {
          queryKey: sellerProcessingKeys.asset(assetId),
        })
      }
    },
  })
}
