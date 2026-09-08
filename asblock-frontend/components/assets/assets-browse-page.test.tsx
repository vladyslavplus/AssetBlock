import { afterEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AssetsBrowsePage } from '@/components/assets/assets-browse-page'
import { renderWithProviders } from '@/test/render'
import type { FetchAssetsPageResult } from '@/lib/catalog/assets-api'
import type { CatalogFacets } from '@/lib/catalog/catalog-query'
import type { CatalogFilters } from '@/lib/catalog/catalog-filters'

const pushMock = vi.fn()
const replaceMock = vi.fn()
let mockSearchParamsString = ''

vi.mock('next/navigation', () => ({
  useRouter: () => ({
    push: pushMock,
    replace: replaceMock,
  }),
  usePathname: () => '/assets',
  useSearchParams: () => new URLSearchParams(mockSearchParamsString),
}))

describe('AssetsBrowsePage UI Component', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    mockSearchParamsString = ''
    vi.clearAllMocks()
  })

  const sampleInitialFilters: CatalogFilters = {
    page: 1,
    pageSize: 12,
    sortBy: 'CreatedAt',
    sortDirection: 'DESC',
    search: '',
    categoryId: '',
    tags: [],
    minPrice: null,
    maxPrice: null,
  }

  const sampleInitialAssets: FetchAssetsPageResult = {
    items: [
      {
        id: '11111111-1111-4111-8111-111111111111',
        title: 'Cyberpunk Asset',
        description: 'Futuristic model',
        price: 19.99,
        categoryId: '22222222-2222-4222-8222-222222222222',
        categoryName: '3D Models',
        authorId: '33333333-3333-4333-8333-333333333333',
        authorUsername: 'creator',
        createdAt: '2026-01-01T00:00:00Z',
        tags: ['cyberpunk', 'sci-fi'],
        averageRating: 4.8,
      },
    ],
    totalCount: 1,
    page: 1,
    pageSize: 12,
    totalPages: 1,
    isTruncated: false,
  }

  const sampleFacets: CatalogFacets = {
    categories: [{ id: '22222222-2222-4222-8222-222222222222', name: '3D Models' }],
    tags: ['cyberpunk', 'sci-fi'],
  }

  it('renders heading, description, and asset cards from initialData', () => {
    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={sampleInitialFilters}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    expect(screen.getByRole('heading', { level: 1, name: /browse assets/i })).toBeInTheDocument()
    expect(screen.getByText(/discover templates, tools, and code packages/i)).toBeInTheDocument()
    // Multiple cards rendered across desktop and mobile containers
    expect(screen.getAllByText('Cyberpunk Asset').length).toBeGreaterThanOrEqual(1)
  })

  it('renders active filter chips when filters are set in query string', async () => {
    mockSearchParamsString =
      'category=22222222-2222-4222-8222-222222222222&tags=cyberpunk&minPrice=10'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          categoryId: '22222222-2222-4222-8222-222222222222',
          tags: ['cyberpunk'],
          minPrice: 10,
        }}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    expect(screen.getAllByText('3D Models').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('cyberpunk').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Min: $10').length).toBeGreaterThanOrEqual(1)
  })

  it('allows clearing filters via clear filters button when empty', async () => {
    const user = userEvent.setup()
    mockSearchParamsString = 'search=nonexistent'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'nonexistent',
          sortBy: 'Relevance',
          sortDirection: 'DESC',
        }}
        initialAssetsResult={{
          items: [],
          totalCount: 0,
          page: 1,
          pageSize: 12,
          totalPages: 0,
          isTruncated: false,
        }}
        initialFacets={sampleFacets}
      />,
    )

    const clearBtns = screen.getAllByRole('button', { name: /clear filters/i })
    expect(clearBtns.length).toBeGreaterThanOrEqual(1)
    await user.click(clearBtns[0])
    expect(pushMock).toHaveBeenCalledWith('/assets')
  })

  it('shows a best-results notice only for a truncated relevance search', () => {
    mockSearchParamsString = 'search=cyberpunk'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'cyberpunk',
          sortBy: 'Relevance',
          sortDirection: 'DESC',
        }}
        initialAssetsResult={{ ...sampleInitialAssets, isTruncated: true }}
        initialFacets={sampleFacets}
      />,
    )

    expect(
      screen.getAllByText(/showing the best matches for your search/i).length,
    ).toBeGreaterThanOrEqual(1)
  })

  it('does not show the best-results notice when relevance search is not truncated', () => {
    mockSearchParamsString = 'search=cyberpunk'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'cyberpunk',
          sortBy: 'Relevance',
          sortDirection: 'DESC',
        }}
        initialAssetsResult={{ ...sampleInitialAssets, isTruncated: false }}
        initialFacets={sampleFacets}
      />,
    )

    expect(screen.queryByText(/showing the best matches for your search/i)).not.toBeInTheDocument()
  })

  it('does not show the best-results notice for an explicit-sort search', () => {
    mockSearchParamsString = 'search=cyberpunk&sortBy=CreatedAt'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'cyberpunk',
          sortBy: 'CreatedAt',
        }}
        initialAssetsResult={{ ...sampleInitialAssets, isTruncated: true }}
        initialFacets={sampleFacets}
      />,
    )

    expect(screen.queryByText(/showing the best matches for your search/i)).not.toBeInTheDocument()
  })

  it('promotes a typed search to relevance mode by omitting sort params from the URL', async () => {
    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={sampleInitialFilters}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    const searchInput = screen.getAllByLabelText(/search/i)[0]
    const user = userEvent.setup({ delay: null })
    await user.type(searchInput, 'sword')

    // The 300ms debounce must fire for the URL to update.
    await vi.waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith('/assets?search=sword')
    })
  })

  it('resets pagination to page 1 when the search term changes', async () => {
    mockSearchParamsString = 'page=3'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{ ...sampleInitialFilters, page: 3 }}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    const searchInput = screen.getAllByLabelText(/search/i)[0]
    const user = userEvent.setup({ delay: null })
    await user.type(searchInput, 'sword')

    await vi.waitFor(() => {
      // Changing the search term resets the page to 1 (no page param) and keeps relevance mode.
      expect(pushMock).toHaveBeenCalledWith('/assets?search=sword')
    })
  })

  it('keeps an explicitly chosen sort in the URL during a search', async () => {
    const user = userEvent.setup({ delay: null })
    mockSearchParamsString = 'search=cyberpunk'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'cyberpunk',
          sortBy: 'Relevance',
          sortDirection: 'DESC',
        }}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    // Open the sort dropdown in the filters sidebar and choose "Newest" (explicit CreatedAt).
    const sortTriggers = screen.getAllByLabelText(/sort by/i)
    await user.click(sortTriggers[0])
    const newestOption = screen.getByRole('menuitem', { name: /newest/i })
    await user.click(newestOption)

    await vi.waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith('/assets?search=cyberpunk&sortBy=CreatedAt')
    })
  })

  it('preserves explicit CreatedAt sort when search term is refined', async () => {
    const user = userEvent.setup({ delay: null })
    mockSearchParamsString = 'search=cyberpunk&sortBy=CreatedAt'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'cyberpunk',
          sortBy: 'CreatedAt',
          sortDirection: 'DESC',
        }}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    const searchInput = screen.getAllByLabelText(/search/i)[0]
    await user.type(searchInput, ' refined')

    await vi.waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith('/assets?search=cyberpunk+refined&sortBy=CreatedAt')
    })
  })

  it('preserves explicit Price sort when search term is refined', async () => {
    const user = userEvent.setup({ delay: null })
    mockSearchParamsString = 'search=blade&sortBy=Price'

    renderWithProviders(
      <AssetsBrowsePage
        initialFilters={{
          ...sampleInitialFilters,
          search: 'blade',
          sortBy: 'Price',
          sortDirection: 'ASC',
        }}
        initialAssetsResult={sampleInitialAssets}
        initialFacets={sampleFacets}
      />,
    )

    const searchInput = screen.getAllByLabelText(/search/i)[0]
    await user.type(searchInput, ' refined')

    await vi.waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith('/assets?search=blade+refined&sortBy=Price')
    })
  })
})
