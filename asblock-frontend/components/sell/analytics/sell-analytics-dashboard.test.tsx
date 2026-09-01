import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'

import { SellAnalyticsDashboard } from '@/components/sell/analytics/sell-analytics-dashboard'
import { renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

const navigation = vi.hoisted(() => ({ replace: vi.fn(), search: '' }))
const useAuth = vi.hoisted(() => vi.fn())
vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace: navigation.replace }),
  usePathname: () => '/sell',
  useSearchParams: () => new URLSearchParams(navigation.search),
}))
vi.mock('@/components/auth/auth-context', () => ({ useAuth: () => useAuth() }))

beforeAll(() => {
  HTMLElement.prototype.scrollIntoView = vi.fn()
})

const countMetric = { current: 0, previous: 0, absoluteChange: 0, percentageChange: null }
const rateMetric = { current: null, previous: null, absoluteChange: null, percentageChange: null }
const overview = {
  from: '2026-08-01',
  to: '2026-09-01',
  comparisonFrom: '2026-07-01',
  comparisonTo: '2026-08-01',
  timezone: 'UTC',
  granularity: 'DAY',
  generatedAt: '2026-09-01T00:00:00.000Z',
  currency: 'usd',
  engagementAvailableFrom: null,
  grossRevenue: countMetric,
  directRevenue: countMetric,
  bundleRevenue: countMetric,
  orders: countMetric,
  unitsSold: countMetric,
  averageOrderValue: countMetric,
  uniqueCustomers: countMetric,
  newCustomers: countMetric,
  returningCustomers: countMetric,
  repeatCustomers: countMetric,
  repeatCustomerRate: rateMetric,
  averageRating: null,
  newReviews: countMetric,
  series: [],
  topAssets: [],
  topBundles: [],
  engagementTotals: null,
  commerceFunnel: null,
  trackedFunnel: null,
  trackedCheckoutCoverage: null,
  trafficSources: null,
}

function analyticsFetch() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    if (url.includes('/overview?')) return Response.json(overview)
    if (url.includes('/products?')) {
      return Response.json({
        from: overview.from,
        to: overview.to,
        timezone: 'UTC',
        currency: 'usd',
        generatedAt: overview.generatedAt,
        items: [],
        totalCount: 0,
        page: 1,
        pageSize: 20,
      })
    }
    if (url.includes('/sales?')) {
      return Response.json({
        from: overview.from,
        to: overview.to,
        timezone: 'UTC',
        currency: 'usd',
        generatedAt: overview.generatedAt,
        items: [],
        hasMore: false,
        nextCursor: null,
      })
    }
    if (url.includes('/collections?')) {
      return Response.json({
        from: overview.from,
        to: overview.to,
        timezone: 'UTC',
        currency: 'usd',
        generatedAt: overview.generatedAt,
        engagementAvailableFrom: null,
        items: [],
        totalCount: 0,
        page: 1,
        pageSize: 20,
      })
    }
    throw new Error(`Unexpected request ${url}`)
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
  navigation.replace.mockReset()
  navigation.search = ''
  useAuth.mockReset()
})

describe('SellAnalyticsDashboard', () => {
  it('renders a loading dashboard while the overview is pending', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise<Response>(() => undefined)),
    )
    useAuth.mockReturnValue({ status: 'authenticated', user: verifiedSeller() })
    renderWithQueryClient(<SellAnalyticsDashboard />)

    expect(screen.getByLabelText('Loading analytics dashboard')).toHaveAttribute(
      'aria-busy',
      'true',
    )
  })

  it('renders signed-out state without analytics requests', () => {
    const fetchMock = analyticsFetch()
    vi.stubGlobal('fetch', fetchMock)
    useAuth.mockReturnValue({ status: 'unauthenticated', user: null })
    renderWithQueryClient(<SellAnalyticsDashboard />)
    expect(screen.getByText(/sign in to view seller analytics/i)).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('renders empty successful state and requests each endpoint once with URL filters', async () => {
    navigation.search = 'tab=analytics&range=7d&productType=BUNDLE'
    const fetchMock = analyticsFetch()
    vi.stubGlobal('fetch', fetchMock)
    useAuth.mockReturnValue({ status: 'authenticated', user: verifiedSeller() })
    renderWithQueryClient(<SellAnalyticsDashboard />)

    expect(await screen.findByText(/no sales in this range/i)).toBeInTheDocument()
    expect(screen.getByText(/no product performance yet/i)).toBeInTheDocument()
    const urls = fetchMock.mock.calls.map(([input]) => String(input))
    expect(urls.filter((url) => url.includes('/overview?'))).toHaveLength(1)
    expect(urls.filter((url) => url.includes('/products?'))).toHaveLength(1)
    expect(urls.filter((url) => url.includes('/sales?'))).toHaveLength(1)
    expect(urls.filter((url) => url.includes('/collections?'))).toHaveLength(1)
    expect(urls.find((url) => url.includes('/products?'))).toContain('productType=BUNDLE')
  })

  it('renders query error without empty-state success messaging', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ detail: 'Backend unavailable' }, { status: 503 })),
    )
    useAuth.mockReturnValue({ status: 'authenticated', user: verifiedSeller() })
    renderWithQueryClient(<SellAnalyticsDashboard />)
    expect(await screen.findByText('Overview unavailable')).toBeInTheDocument()
    expect(screen.queryByText(/no sales in this range/i)).not.toBeInTheDocument()
  })

  it('formats commerce metrics and product values from a successful response', async () => {
    const product = {
      productKind: 'ASSET',
      productId: '123e4567-e89b-42d3-a456-426614174000',
      title: 'Terrain Pro',
      availability: 'ACTIVE',
      grossRevenueCents: 987654,
      directRevenueCents: 900000,
      bundleAllocatedRevenueCents: 87654,
      orders: 1200,
      unitsSold: 1400,
      averageRating: 4.75,
      reviewCount: 80,
      latestSaleAt: '2026-08-31T12:00:00.000Z',
      currentPriceCents: 2500,
      listPriceCents: 2500,
      discountPercent: 0,
    }
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('/overview?')) {
          return Response.json({
            ...overview,
            grossRevenue: {
              current: 12345,
              previous: 10000,
              absoluteChange: 2345,
              percentageChange: 0.2345,
            },
            orders: { current: 1200, previous: 1000, absoluteChange: 200, percentageChange: 0.2 },
          })
        }
        if (url.includes('/products?')) {
          return Response.json({
            from: overview.from,
            to: overview.to,
            timezone: 'UTC',
            currency: 'usd',
            generatedAt: overview.generatedAt,
            items: [product],
            totalCount: 1,
            page: 1,
            pageSize: 20,
          })
        }
        if (url.includes('/sales?')) {
          return Response.json({
            from: overview.from,
            to: overview.to,
            timezone: 'UTC',
            currency: 'usd',
            generatedAt: overview.generatedAt,
            items: [],
            hasMore: false,
            nextCursor: null,
          })
        }
        return Response.json({
          from: overview.from,
          to: overview.to,
          timezone: 'UTC',
          currency: 'usd',
          generatedAt: overview.generatedAt,
          engagementAvailableFrom: null,
          items: [],
          totalCount: 0,
          page: 1,
          pageSize: 20,
        })
      }),
    )
    useAuth.mockReturnValue({ status: 'authenticated', user: verifiedSeller() })
    renderWithQueryClient(<SellAnalyticsDashboard />)

    expect(
      await screen.findByRole('article', { name: 'Gross revenue: $123.45' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('article', { name: 'Orders: 1,200' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Terrain Pro' })).toBeInTheDocument()
    expect(screen.getByText('$9,876.54')).toBeInTheDocument()
    expect(screen.getByText('4.8')).toBeInTheDocument()
  })

  it('writes product filter selection to URL state without duplicate requests', async () => {
    const fetchMock = analyticsFetch()
    vi.stubGlobal('fetch', fetchMock)
    useAuth.mockReturnValue({ status: 'authenticated', user: verifiedSeller() })
    renderWithQueryClient(<SellAnalyticsDashboard />)
    const productFilter = await screen.findByRole('combobox', { name: 'Filter product type' })
    navigation.replace.mockClear()
    fireEvent.keyDown(productFilter, { key: 'ArrowDown' })
    fireEvent.click(await screen.findByRole('option', { name: 'Assets' }))

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledOnce())
    expect(String(navigation.replace.mock.calls[0]?.[0])).toContain('productType=ASSET')
    expect(
      fetchMock.mock.calls.filter(([input]) => String(input).includes('/overview?')),
    ).toHaveLength(1)
  })
})
