import { QueryClient } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { DEFAULT_CATALOG_FILTERS } from '@/lib/catalog/catalog-filters'
import { catalogKeys, fetchCatalogPage } from '@/lib/catalog/catalog-query'
import { collectionKeys } from '@/lib/collections/collections-query'
import { bundleKeys } from '@/lib/bundles/bundles-query'
import { analyticsKeys } from '@/lib/analytics/analytics-query'
import { sellerKeys } from '@/lib/seller/seller-query'
import { libraryKeys } from '@/lib/library/library-query'
import { notificationsKeys } from '@/lib/notifications/notifications-query'
import { authKeys } from '@/lib/auth/auth-query'
import { accountKeys } from '@/lib/account/account-query'
import { clearPrivateUserQueries } from '@/lib/query/clear-user-scoped-queries'
import { runQueryInBackground } from '@/lib/query/query-refresh'
import { createTestQueryClient } from '@/test/query-client'

describe('query keys', () => {
  it('includes filters, paging, sort, range, and entity ids', () => {
    const catalog = catalogKeys.list({
      ...DEFAULT_CATALOG_FILTERS,
      search: 'shader',
      categoryId: 'cat-1',
      tags: ['unity'],
      page: 2,
    })
    expect(catalog).toEqual([
      'catalog',
      'list',
      expect.objectContaining({ search: 'shader', categoryId: 'cat-1', page: 2, tags: ['unity'] }),
    ])

    const filters = { page: 2, search: 'pack', sortBy: 'Title', sortDirection: 'ASC' as const }
    expect(collectionKeys.publicList(filters)).toEqual(['collections', 'public', 'list', filters])
    expect(collectionKeys.sellerDetail('col-1')).toEqual([
      'collections',
      'seller',
      'detail',
      'col-1',
    ])
    expect(bundleKeys.publicList(filters)).toEqual(['bundles', 'public', 'list', filters])
    expect(bundleKeys.sellerDetail('bun-1')).toEqual(['bundles', 'seller', 'detail', 'bun-1'])

    const range = { from: '2026-01-01', to: '2026-02-01' }
    expect(analyticsKeys.overview(range)).toEqual([
      'seller',
      'analytics',
      'overview',
      range.from,
      range.to,
    ])
    expect(
      analyticsKeys.products(range, {
        productType: 'ASSET',
        sort: 'REVENUE',
        direction: 'DESC',
        page: 3,
        pageSize: 20,
      }),
    ).toEqual([
      'seller',
      'analytics',
      'products',
      '2026-01-01',
      '2026-02-01',
      'ASSET',
      'REVENUE',
      'DESC',
      3,
      20,
    ])
    expect(analyticsKeys.assetDetail('asset-1', range)).toEqual([
      'seller',
      'analytics',
      'asset',
      'asset-1',
      '2026-01-01',
      '2026-02-01',
    ])
  })
})

describe('session cache isolation', () => {
  it('clears private user keys and keeps public catalog and session cache', () => {
    const client = createTestQueryClient()
    client.setQueryData(catalogKeys.list(DEFAULT_CATALOG_FILTERS), { items: ['public'] })
    client.setQueryData(collectionKeys.publicList({ page: 1 }), { items: ['public-col'] })
    client.setQueryData(sellerKeys.listings(), { items: ['mine'] })
    client.setQueryData(libraryKeys.purchases(), { items: [] })
    client.setQueryData(notificationsKeys.inbox(), { items: [] })
    client.setQueryData(authKeys.session(), { id: 'u1' })
    client.setQueryData(accountKeys.me(), { username: 'seller' })
    client.setQueryData(collectionKeys.sellerList(), { items: [] })

    clearPrivateUserQueries(client)

    expect(client.getQueryData(catalogKeys.list(DEFAULT_CATALOG_FILTERS))).toEqual({
      items: ['public'],
    })
    expect(client.getQueryData(collectionKeys.publicList({ page: 1 }))).toEqual({
      items: ['public-col'],
    })
    expect(client.getQueryData(authKeys.session())).toEqual({ id: 'u1' })
    expect(client.getQueryData(sellerKeys.listings())).toBeUndefined()
    expect(client.getQueryData(libraryKeys.purchases())).toBeUndefined()
    expect(client.getQueryData(notificationsKeys.inbox())).toBeUndefined()
    expect(client.getQueryData(accountKeys.me())).toBeUndefined()
    expect(client.getQueryData(collectionKeys.sellerList())).toBeUndefined()
  })
})

describe('background query failures', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('swallows AbortError and does not leave an unhandled rejection', async () => {
    const abort = new DOMException('aborted', 'AbortError')
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const unhandled: unknown[] = []
    const onUnhandled = (reason: unknown) => {
      unhandled.push(reason)
    }
    process.on('unhandledRejection', onUnhandled)
    runQueryInBackground(Promise.reject(abort))
    await vi.waitFor(() => {
      expect(errorSpy).not.toHaveBeenCalled()
    })
    await Promise.resolve()
    process.off('unhandledRejection', onUnhandled)
    expect(unhandled).toEqual([])
  })

  it('logs non-abort failures', async () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    runQueryInBackground(Promise.reject(new Error('boom')))
    await vi.waitFor(() => {
      expect(errorSpy).toHaveBeenCalled()
    })
  })
})

describe('catalog fetcher cancellation', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('passes the query AbortSignal through to fetch', async () => {
    const controller = new AbortController()
    let seen: AbortSignal | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        seen = init?.signal ?? (undefined as AbortSignal)
        return new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 12 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )
    await fetchCatalogPage(DEFAULT_CATALOG_FILTERS, controller.signal)
    expect(seen).toBe(controller.signal)
  })
})

describe('seller listings cancellation', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('forwards the query AbortSignal to fetch', async () => {
    const { fetchSellerListingsQuery } = await import('@/lib/seller/seller-query')
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

  it('does not treat AbortError as a query error state when using QueryClient', async () => {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    })
    const abortError = new DOMException('aborted', 'AbortError')
    const result = await client
      .fetchQuery({
        queryKey: ['probe'],
        queryFn: async () => {
          throw abortError
        },
      })
      .then(
        () => 'ok',
        (error: unknown) => error,
      )
    expect(result).toBe(abortError)
    const { isAbortError } = await import('@/lib/http/is-abort-error')
    expect(isAbortError(result)).toBe(true)
    client.clear()
  })
})
