import { afterEach, describe, expect, it, vi } from 'vitest'

import { fetchSellerListingsQuery } from '@/lib/seller/seller-query'

describe('fetchSellerListingsQuery', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('forwards the query AbortSignal to fetchMyListings', async () => {
    const controller = new AbortController()
    let seen: AbortSignal | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        seen = init?.signal ?? undefined
        return new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 50 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )

    await fetchSellerListingsQuery({ signal: controller.signal })
    expect(seen).toBe(controller.signal)
  })
})
