import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  assetKeys,
  fetchPersonalSimilarAssets,
  fetchSimilarAssetsPublic,
  isPersonalSimilarKey,
  personalSimilarAssetsQueryOptions,
  similarAssetsQueryOptions,
} from '@/lib/catalog/asset-detail-query'

const sampleItem = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Peer Pack',
  description: 'Close match',
  price: 12,
  categoryId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  categoryName: 'Tools',
  authorId: '11111111-1111-4111-8111-111111111111',
  authorUsername: 'maker',
  createdAt: '2026-01-01T00:00:00.000Z',
  tags: ['lowpoly'],
  averageRating: 4.2,
}

const secondItem = {
  ...sampleItem,
  id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  title: 'Second Pack',
}

function similarPayloadResponse(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('fetchSimilarAssetsPublic', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('requests source and limit and maps public card items', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ items: [sampleItem] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(
        '/api/assets/cccccccc-cccc-4ccc-8ccc-cccccccccccc/similar?limit=6&mode=similarity',
      ),
      expect.objectContaining({ method: 'GET' }),
    )
    expect(result.items).toEqual([
      {
        ...sampleItem,
        price: 12,
        averageRating: 4.2,
      },
    ])
    expect(result.exposure).toBeNull()
  })

  it('uses a similar-only query prefix and disables stale-while-revalidate', () => {
    const options = similarAssetsQueryOptions('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)
    expect(options.queryKey).toEqual([
      'assets',
      'similar',
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'similarity',
    ])
    expect(assetKeys.similarAll).toEqual(['assets', 'similar'])
    expect(options.staleTime).toBe(0)
    expect(options.refetchOnMount).toBe(true)
    expect(options.refetchOnWindowFocus).toBe(true)
  })

  it('isolates popularity mode in query key and request URL', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ items: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const options = similarAssetsQueryOptions(
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'popularity',
    )
    expect(options.queryKey).toEqual([
      'assets',
      'similar',
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'popularity',
    ])
    await options.queryFn()
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('similar?limit=6&mode=popularity'),
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('keys personal similar queries by user and hits the account BFF route', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ items: [], exposure: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const userId = '22222222-2222-4222-8222-222222222222'

    const options = personalSimilarAssetsQueryOptions(
      userId,
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'similarity',
    )
    expect(options.queryKey).toEqual([
      'assets',
      'similar',
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'similarity',
      'personal',
      userId,
    ])
    const result = await fetchPersonalSimilarAssets(
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'similarity',
    )
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/account/assets/cccccccc-cccc-4ccc-8ccc-cccccccccccc/similar?limit=6&mode=similarity',
      expect.objectContaining({ credentials: 'include' }),
    )
    expect(result).toEqual({ items: [], exposure: null, explanations: [] })
  })

  it('maps valid explanations and drops malformed entries', async () => {
    const fetchMock = vi.fn(async () =>
      similarPayloadResponse({
        items: [sampleItem, secondItem],
        exposure: null,
        explanations: [
          {
            assetId: sampleItem.id,
            code: 'SHARED_TAGS',
            text: 'Shares 1 tag with this asset.',
          },
          { assetId: '', code: 'SHARED_TAGS', text: 'Bad id.' },
          { assetId: secondItem.id, code: '', text: 'Bad code.' },
          { assetId: secondItem.id, code: 'SAME_CATEGORY', text: '' },
          'not-an-object',
          null,
          {
            assetId: secondItem.id,
            code: 'SAME_CATEGORY',
            text: 'Similar asset in the same category.',
          },
        ],
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result.explanations).toEqual([
      {
        assetId: sampleItem.id,
        code: 'SHARED_TAGS',
        text: 'Shares 1 tag with this asset.',
      },
      {
        assetId: secondItem.id,
        code: 'SAME_CATEGORY',
        text: 'Similar asset in the same category.',
      },
    ])
  })

  it('clears explanations when mapped items are empty', async () => {
    const fetchMock = vi.fn(async () =>
      similarPayloadResponse({
        items: [],
        exposure: null,
        explanations: [
          {
            assetId: sampleItem.id,
            code: 'SHARED_TAGS',
            text: 'Shares 1 tag with this asset.',
          },
        ],
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result).toEqual({ items: [], exposure: null, explanations: [] })
  })

  it('drops explanations whose assetId is not in returned items', async () => {
    const fetchMock = vi.fn(async () =>
      similarPayloadResponse({
        items: [sampleItem],
        exposure: null,
        explanations: [
          {
            assetId: secondItem.id,
            code: 'SAME_CATEGORY',
            text: 'Similar asset in the same category.',
          },
          {
            assetId: sampleItem.id,
            code: 'SHARED_TAGS',
            text: 'Shares 1 tag with this asset.',
          },
        ],
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result.explanations).toEqual([
      {
        assetId: sampleItem.id,
        code: 'SHARED_TAGS',
        text: 'Shares 1 tag with this asset.',
      },
    ])
  })

  it('reorders explanations to match returned items', async () => {
    const fetchMock = vi.fn(async () =>
      similarPayloadResponse({
        items: [sampleItem, secondItem],
        exposure: null,
        explanations: [
          {
            assetId: secondItem.id,
            code: 'SAME_CATEGORY',
            text: 'Similar asset in the same category.',
          },
          {
            assetId: sampleItem.id,
            code: 'SHARED_TAGS',
            text: 'Shares 2 tags with this asset.',
          },
        ],
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result.explanations.map((explanation) => explanation.assetId)).toEqual([
      sampleItem.id,
      secondItem.id,
    ])
    expect(result.explanations.map((explanation) => explanation.text)).toEqual([
      'Shares 2 tags with this asset.',
      'Similar asset in the same category.',
    ])
  })

  it('keeps the first valid explanation when the same assetId is duplicated', async () => {
    const fetchMock = vi.fn(async () =>
      similarPayloadResponse({
        items: [sampleItem],
        exposure: null,
        explanations: [
          {
            assetId: sampleItem.id,
            code: 'SHARED_TAGS',
            text: 'Shares 1 tag with this asset.',
          },
          {
            assetId: sampleItem.id,
            code: 'SAME_CATEGORY',
            text: 'Similar asset in the same category.',
          },
        ],
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result.explanations).toEqual([
      {
        assetId: sampleItem.id,
        code: 'SHARED_TAGS',
        text: 'Shares 1 tag with this asset.',
      },
    ])
  })

  it('keeps item-relative order when some returned items have no explanation', async () => {
    const thirdItem = {
      ...sampleItem,
      id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      title: 'Third Pack',
    }
    const fetchMock = vi.fn(async () =>
      similarPayloadResponse({
        items: [sampleItem, secondItem, thirdItem],
        exposure: null,
        explanations: [
          {
            assetId: thirdItem.id,
            code: 'POPULAR_IN_CATEGORY',
            text: 'Popular in this category.',
          },
          {
            assetId: sampleItem.id,
            code: 'SHARED_TAGS',
            text: 'Shares 1 tag with this asset.',
          },
        ],
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result.items.map((item) => item.id)).toEqual([
      sampleItem.id,
      secondItem.id,
      thirdItem.id,
    ])
    expect(result.explanations).toEqual([
      {
        assetId: sampleItem.id,
        code: 'SHARED_TAGS',
        text: 'Shares 1 tag with this asset.',
      },
      {
        assetId: thirdItem.id,
        code: 'POPULAR_IN_CATEGORY',
        text: 'Popular in this category.',
      },
    ])
  })

  it('defaults missing explanations to empty', async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ items: [], exposure: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchSimilarAssetsPublic('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 6)

    expect(result.explanations).toEqual([])
  })

  it('matches only user-scoped personal keys for clearing', () => {
    const userId = '22222222-2222-4222-8222-222222222222'
    expect(
      isPersonalSimilarKey(assetKeys.personalSimilar(userId, 'asset-1', 6, 'similarity')),
    ).toBe(true)
    expect(isPersonalSimilarKey(assetKeys.similar('asset-1', 6, 'similarity'))).toBe(false)
    expect(isPersonalSimilarKey(['assets', 'similar'])).toBe(false)
    expect(isPersonalSimilarKey(['assets', 'detail', 'asset-1'])).toBe(false)
  })
})
