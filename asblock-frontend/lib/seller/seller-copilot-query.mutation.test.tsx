import { QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  sellerCopilotKeys,
  useEnqueueListingCopilotMutation,
} from '@/lib/seller/seller-copilot-query'
import { sellerProcessingKeys } from '@/lib/seller/seller-processing-query'
import { createTestQueryClient } from '@/test/query-client'

describe('useEnqueueListingCopilotMutation', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('invalidates copilot and processing keys after enqueue', async () => {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              jobId: '11111111-1111-4111-8111-111111111111',
              assetVersionId: '33333333-3333-4333-8333-333333333333',
            }),
            { status: 202, headers: { 'Content-Type': 'application/json' } },
          ),
      ),
    )

    const { result } = renderHook(
      () =>
        useEnqueueListingCopilotMutation(
          '22222222-2222-4222-8222-222222222222',
          '33333333-3333-4333-8333-333333333333',
        ),
      {
        wrapper: ({ children }) => (
          <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
        ),
      },
    )

    result.current.mutate()
    await waitFor(() => expect(result.current.isSuccess).toBe(true))
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerCopilotKeys.version('33333333-3333-4333-8333-333333333333'),
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerProcessingKeys.version('33333333-3333-4333-8333-333333333333'),
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerProcessingKeys.asset('22222222-2222-4222-8222-222222222222'),
      }),
      expect.anything(),
    )
  })
})
