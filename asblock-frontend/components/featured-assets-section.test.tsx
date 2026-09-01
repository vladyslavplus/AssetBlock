import { afterEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { FeaturedAssetsSection } from '@/components/featured-assets-section'
import { renderWithQueryClient } from '@/test/render'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import { DEFAULT_FEATURED_LIMIT } from '@/lib/catalog/catalog-query'

describe('FeaturedAssetsSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const sampleFeatured: AssetListItem[] = [
    {
      id: '11111111-1111-4111-8111-111111111111',
      title: 'Neon Skyline Asset',
      description: 'Futuristic city models',
      price: 29.99,
      categoryId: '22222222-2222-4222-8222-222222222222',
      categoryName: 'Environments',
      authorId: '33333333-3333-4333-8333-333333333333',
      authorUsername: 'city_builder',
      createdAt: '2026-01-01T00:00:00Z',
      tags: ['city', 'neon'],
      averageRating: 4.9,
    },
  ]

  it('renders server-provided initialAssets without initial network fetch', () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)

    renderWithQueryClient(<FeaturedAssetsSection initialAssets={sampleFeatured} />)

    expect(screen.getByText('Neon Skyline Asset')).toBeInTheDocument()
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('falls back to client query when initialAssets is not provided', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ items: sampleFeatured, totalCount: 1 }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    renderWithQueryClient(<FeaturedAssetsSection />)

    expect(await screen.findByText('Neon Skyline Asset')).toBeInTheDocument()
  })

  it('uses canonical DEFAULT_FEATURED_LIMIT', () => {
    expect(DEFAULT_FEATURED_LIMIT).toBe(8)
  })
})
