import { describe, expect, it, vi, beforeEach } from 'vitest'
import AssetsPage from '@/app/assets/page'
import * as catalogServer from '@/lib/server/catalog-server'
import type { FetchAssetsPageResult } from '@/lib/catalog/assets-api'
import type { CatalogFacets } from '@/lib/catalog/catalog-query'

vi.mock('@/components/assets/assets-browse-page', () => ({
  AssetsBrowsePage: (props: unknown) => (
    <div data-testid="assets-browse-page" data-props={JSON.stringify(props)} />
  ),
}))

describe('AssetsPage Server Component Shell', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('runs catalog page and facets fetches in parallel and normalizes URL params', async () => {
    let listStarted = false
    let facetsStarted = false
    let resolveList!: (val: FetchAssetsPageResult | null) => void
    let resolveFacets!: (val: CatalogFacets | null) => void

    const listPromise = new Promise<FetchAssetsPageResult | null>((resolve) => {
      resolveList = resolve
    })
    const facetsPromise = new Promise<CatalogFacets | null>((resolve) => {
      resolveFacets = resolve
    })

    const getCatalogPageSpy = vi
      .spyOn(catalogServer, 'getCatalogPageCached')
      .mockImplementation(async () => {
        listStarted = true
        return listPromise
      })

    const _getCatalogFacetsSpy = vi
      .spyOn(catalogServer, 'getCatalogFacetsCached')
      .mockImplementation(async () => {
        facetsStarted = true
        return facetsPromise
      })

    const pageExecution = AssetsPage({
      searchParams: Promise.resolve({
        page: '2junk', // should normalize to 1
        categoryId: 'not-a-uuid', // should normalize to ''
        search: 'dragon',
      }),
    })

    // Allow microtask to resolve searchParams
    await Promise.resolve()

    // Assert both server loaders started concurrently before either resolved
    expect(listStarted).toBe(true)
    expect(facetsStarted).toBe(true)
    expect(getCatalogPageSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        categoryId: '',
        search: 'dragon',
      }),
    )

    resolveList({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 12,
      totalPages: 0,
    })
    resolveFacets({
      categories: [],
      tags: [],
    })

    const element = await pageExecution
    expect(element).toBeDefined()
  })
})
