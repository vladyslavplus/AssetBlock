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
        initialFilters={{ ...sampleInitialFilters, search: 'nonexistent' }}
        initialAssetsResult={{
          items: [],
          totalCount: 0,
          page: 1,
          pageSize: 12,
          totalPages: 0,
        }}
        initialFacets={sampleFacets}
      />,
    )

    const clearBtns = screen.getAllByRole('button', { name: /clear filters/i })
    expect(clearBtns.length).toBeGreaterThanOrEqual(1)
    await user.click(clearBtns[0])
    expect(pushMock).toHaveBeenCalledWith('/assets')
  })
})
