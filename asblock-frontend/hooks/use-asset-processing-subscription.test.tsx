import { QueryClientProvider } from '@tanstack/react-query'
import { act, render, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useForm } from 'react-hook-form'

import { AssetProcessingStatusPanel } from '@/components/sell/asset-processing-status-panel'
import { ListingCopilotPanel } from '@/components/sell/listing-copilot-panel'
import { useAssetProcessingSubscription } from '@/hooks/use-asset-processing-subscription'
import type { AssetEditFormValues } from '@/lib/seller/seller-schemas'
import { sellerCopilotKeys } from '@/lib/seller/seller-copilot-query'
import { sellerKeys } from '@/lib/seller/seller-query'
import { sellerProcessingKeys } from '@/lib/seller/seller-processing-query'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { assetKeys } from '@/lib/catalog/asset-detail-query'
import {
  setHubConnectionState,
  _resetHubConnectionStateForTest,
} from '@/lib/notifications/hub-connection-state'
import { createTestQueryClient } from '@/test/query-client'

const subscribeProcessingHub = vi.hoisted(() => vi.fn())
const ownerUserId = '11111111-1111-4111-8111-111111111111'

vi.mock('@/lib/notifications/notification-hub', () => ({
  subscribeProcessingHub: (cb: (msg: unknown) => void, userId: string) =>
    subscribeProcessingHub(cb, userId),
}))

describe('useAssetProcessingSubscription', () => {
  beforeEach(() => {
    subscribeProcessingHub.mockReset()
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('does not subscribe without a user id', () => {
    const queryClient = createTestQueryClient()
    renderHook(() => useAssetProcessingSubscription(true, null), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      ),
    })
    expect(subscribeProcessingHub).not.toHaveBeenCalled()
  })

  it('invalidates copilot version key for LISTING_COPILOT updates', async () => {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    let handler: ((msg: unknown) => void) | undefined
    subscribeProcessingHub.mockImplementation((cb: (msg: unknown) => void) => {
      handler = cb
      return () => {}
    })

    renderHook(() => useAssetProcessingSubscription(true, ownerUserId), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      ),
    })

    await waitFor(() => expect(handler).toBeDefined())
    handler?.({
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'LISTING_COPILOT',
      status: 'SUCCEEDED',
      stage: 'SUCCEEDED',
      updatedAt: '2026-08-25T12:00:00Z',
    })

    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerProcessingKeys.asset('22222222-2222-4222-8222-222222222222'),
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
        queryKey: sellerCopilotKeys.version('33333333-3333-4333-8333-333333333333'),
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerKeys.detail('22222222-2222-4222-8222-222222222222'),
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerKeys.listings(),
      }),
      expect.anything(),
    )
    expect(invalidate).not.toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: catalogKeys.all,
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledTimes(6)
  })

  it('invalidates each key once when both edit panels share a single parent subscription', async () => {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    let handler: ((msg: unknown) => void) | undefined
    subscribeProcessingHub.mockImplementation((cb: (msg: unknown) => void) => {
      handler = cb
      return () => {}
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (String(url).includes('processing-jobs')) {
          return new Response(JSON.stringify([]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        return new Response('', { status: 404 })
      }),
    )

    function EditSurface() {
      const form = useForm<AssetEditFormValues>({
        defaultValues: {
          title: 'Old',
          description: '',
          price: 1,
          categoryId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
          tags: '',
        },
      })
      useAssetProcessingSubscription(true, ownerUserId)
      return (
        <>
          <ListingCopilotPanel
            assetId="22222222-2222-4222-8222-222222222222"
            assetVersionId="33333333-3333-4333-8333-333333333333"
            categories={[{ id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', name: '3D' }]}
            catalogTags={['lowpoly']}
            setValue={form.setValue}
          />
          <AssetProcessingStatusPanel
            assetId="22222222-2222-4222-8222-222222222222"
            assetVersionId="33333333-3333-4333-8333-333333333333"
          />
        </>
      )
    }

    render(
      <QueryClientProvider client={queryClient}>
        <EditSurface />
      </QueryClientProvider>,
    )

    await waitFor(() => expect(handler).toBeDefined())
    expect(subscribeProcessingHub).toHaveBeenCalledTimes(1)
    expect(subscribeProcessingHub).toHaveBeenCalledWith(expect.any(Function), ownerUserId)

    handler?.({
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'LISTING_COPILOT',
      status: 'SUCCEEDED',
      stage: 'SUCCEEDED',
      updatedAt: '2026-08-25T12:00:00Z',
    })

    const keys = invalidate.mock.calls.map((call) => JSON.stringify(call[0]))
    expect(
      keys.filter((key) =>
        key.includes(
          JSON.stringify(sellerProcessingKeys.asset('22222222-2222-4222-8222-222222222222')),
        ),
      ),
    ).toHaveLength(1)
    expect(
      keys.filter((key) =>
        key.includes(
          JSON.stringify(sellerProcessingKeys.version('33333333-3333-4333-8333-333333333333')),
        ),
      ),
    ).toHaveLength(1)
    expect(
      keys.filter((key) =>
        key.includes(
          JSON.stringify(sellerCopilotKeys.version('33333333-3333-4333-8333-333333333333')),
        ),
      ),
    ).toHaveLength(1)
    expect(
      keys.filter((key) =>
        key.includes(JSON.stringify(sellerKeys.detail('22222222-2222-4222-8222-222222222222'))),
      ),
    ).toHaveLength(1)
    expect(keys.filter((key) => key.includes(JSON.stringify(sellerKeys.listings())))).toHaveLength(
      1,
    )
  })

  it('invalidates catalog once for a security terminal update', async () => {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    let handler: ((msg: unknown) => void) | undefined
    subscribeProcessingHub.mockImplementation((cb: (msg: unknown) => void) => {
      handler = cb
      return () => {}
    })

    renderHook(() => useAssetProcessingSubscription(true, ownerUserId), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      ),
    })

    await waitFor(() => expect(handler).toBeDefined())
    handler?.({
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'MALWARE_SCAN',
      status: 'SUCCEEDED',
      stage: 'READY',
      updatedAt: '2026-08-25T12:00:00Z',
    })

    const keys = invalidate.mock.calls.map((call) => JSON.stringify(call[0]))
    expect(keys.filter((key) => key.includes(JSON.stringify(catalogKeys.all)))).toHaveLength(1)
    expect(
      keys.filter((key) =>
        key.includes(JSON.stringify(assetKeys.detail('22222222-2222-4222-8222-222222222222'))),
      ),
    ).toHaveLength(1)
    expect(invalidate).toHaveBeenCalledTimes(7)
  })

  it('triggers catch-up invalidations when SignalR transitions to connected state', async () => {
    _resetHubConnectionStateForTest()
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    const { rerender } = renderHook(() => useAssetProcessingSubscription(true, ownerUserId), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      ),
    })

    expect(invalidate).not.toHaveBeenCalled()

    // Transition to connected triggers catch-up invalidations once across seller, catalog, asset
    act(() => {
      setHubConnectionState('connected')
    })

    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: sellerKeys.all,
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: catalogKeys.all,
      }),
      expect.anything(),
    )
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: assetKeys.all,
      }),
      expect.anything(),
    )
    const callCount = invalidate.mock.calls.length
    expect(callCount).toBe(3)

    // Re-render without state change does NOT fire catch-up again
    rerender()
    expect(invalidate).toHaveBeenCalledTimes(callCount)

    // Disconnecting and reconnecting fires catch-up again
    act(() => {
      setHubConnectionState('reconnecting')
    })
    act(() => {
      setHubConnectionState('connected')
    })
    expect(invalidate).toHaveBeenCalledTimes(callCount + 3)
  })
})
