import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { SimilarAssetsBlock } from '@/components/assets/similar-assets-block'
import { accountKeys } from '@/lib/account/account-query'
import type { SessionUser } from '@/lib/auth/auth-types'
import { resetRecommendationSignalDedupeForTests } from '@/lib/analytics/recommendation-telemetry'
import { renderWithProviders, renderWithQueryClient } from '@/test/render'

const authUser: SessionUser = {
  id: '22222222-2222-4222-8222-222222222222',
  username: 'buyer',
  role: 'User',
  emailVerifiedAt: '2026-01-01T00:00:00.000Z',
  avatarUrl: null,
  bio: null,
  isPublicProfile: true,
  createdAt: '2026-01-01T00:00:00.000Z',
  socialLinks: [],
}

const sourceId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const peer = {
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

const exposure = {
  id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  rankingVersion: 'similar-assets-v1-metadata',
  expiresAt: '2026-09-13T12:15:00.000Z',
  token: 'a'.repeat(64),
}

function similarPayload(items: unknown[], nextExposure: unknown = exposure) {
  return JSON.stringify({ items, exposure: nextExposure })
}

class FakeIntersectionObserver {
  static instance: FakeIntersectionObserver | null = null
  callback: IntersectionObserverCallback
  observed: Element[] = []

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback
    FakeIntersectionObserver.instance = this
  }

  observe(element: Element) {
    this.observed.push(element)
  }

  unobserve() {}

  disconnect() {
    this.observed = []
  }

  trigger(isIntersecting: boolean, ratio: number) {
    const entries = this.observed.map((target) => ({
      target,
      isIntersecting,
      intersectionRatio: ratio,
    })) as IntersectionObserverEntry[]
    this.callback(entries, this as unknown as IntersectionObserver)
  }
}

describe('SimilarAssetsBlock', () => {
  beforeEach(() => {
    vi.unstubAllGlobals()
    resetRecommendationSignalDedupeForTests()
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('shows a compact loading skeleton without blocking sibling content', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>(() => {
            /* pending */
          }),
      ),
    )

    renderWithQueryClient(
      <div>
        <p>Purchase stays visible</p>
        <SimilarAssetsBlock assetId={sourceId} />
      </div>,
    )

    expect(screen.getByText('Purchase stays visible')).toBeInTheDocument()
    expect(screen.getByLabelText(/loading similar assets/i)).toBeInTheDocument()
  })

  it('hides the block when there are no similar assets', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(similarPayload([]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)

    await waitFor(() => {
      expect(screen.queryByRole('heading', { name: /similar assets/i })).not.toBeInTheDocument()
    })
  })

  it('renders reusable catalog cards for similar assets', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(similarPayload([peer]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)

    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /similar assets/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /view details/i })).toHaveAttribute(
      'href',
      `/assets/${peer.id}?src=catalog`,
    )
  })

  it('keeps a local retry state when the similar request fails', async () => {
    const user = userEvent.setup()
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('{"title":"fail"}', { status: 500 }))
      .mockResolvedValueOnce(
        new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      )
    vi.stubGlobal('fetch', fetchMock)

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)

    expect(await screen.findByText(/similar assets are unavailable/i)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /retry/i }))
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
  })

  it('sends one impression after a card stays 50% visible for one second', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/similar')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(null, { status: 202 })
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    vi.useFakeTimers()

    FakeIntersectionObserver.instance!.trigger(true, 0.5)
    await vi.advanceTimersByTimeAsync(1000)

    const recCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('recommendation-events'),
    )
    expect(recCalls).toHaveLength(1)
    const body = JSON.parse(String(recCalls[0]?.[1]?.body)) as { eventType: string }
    expect(body.eventType).toBe('IMPRESSION')
  })

  it('does not double-fire an impression when the same exposure is observed again', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/similar')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(null, { status: 202 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const { unmount } = renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    vi.useFakeTimers()
    FakeIntersectionObserver.instance!.trigger(true, 0.5)
    await vi.advanceTimersByTimeAsync(1000)
    unmount()
    vi.useRealTimers()

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    vi.useFakeTimers()
    FakeIntersectionObserver.instance!.trigger(true, 0.5)
    await vi.advanceTimersByTimeAsync(1000)

    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('recommendation-events')),
    ).toHaveLength(1)
  })

  it('sends a recommendation click without treating destination ASSET_VIEW as attribution', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/similar')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(null, { status: 202 })
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: /view details/i }))

    const recCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('recommendation-events'),
    )
    expect(recCalls).toHaveLength(1)
    const body = JSON.parse(String(recCalls[0]?.[1]?.body)) as { eventType: string }
    expect(body.eventType).toBe('CLICK')
  })

  it('requests similarity mode by default and switches to popularity mode on demand', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL): Promise<Response> =>
        new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()

    const similarCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('mode=similarity'),
    )
    expect(similarCalls.length).toBeGreaterThan(0)
    expect(fetchMock.mock.calls.some((call) => String(call[0]).includes('mode=popularity'))).toBe(
      false,
    )

    await user.click(screen.getByRole('button', { name: /popular/i }))

    await waitFor(() => {
      expect(fetchMock.mock.calls.some((call) => String(call[0]).includes('mode=popularity'))).toBe(
        true,
      )
    })
    expect(screen.getByRole('button', { name: /popular/i })).toHaveAttribute('aria-pressed', 'true')
  })

  it('skips recommendation beacons when Do Not Track is enabled', async () => {
    const user = userEvent.setup()
    // Restore afterwards: a leaked opt-out would suppress beacons in later tests.
    const originalDoNotTrack = navigator.doNotTrack
    Object.defineProperty(navigator, 'doNotTrack', {
      configurable: true,
      value: '1',
      writable: true,
    })
    try {
      const fetchMock = vi.fn(async (_input: RequestInfo | URL) => {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      })
      vi.stubGlobal('fetch', fetchMock)

      renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)
      expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
      await user.click(screen.getByRole('link', { name: /view details/i }))

      expect(
        fetchMock.mock.calls.filter((call) => String(call[0]).includes('recommendation-events')),
      ).toHaveLength(0)
    } finally {
      Object.defineProperty(navigator, 'doNotTrack', {
        configurable: true,
        value: originalDoNotTrack,
        writable: true,
      })
    }
  })

  it('uses the public endpoint for opted-out authenticated users', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: false, optedInAt: null })
      }
      return new Response(similarPayload([peer]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    const urls = fetchMock.mock.calls.map((call) => String(call[0]))
    expect(urls.some((url) => url.includes('/api/assets/'))).toBe(true)
    expect(urls.some((url) => url.includes('/api/account/assets/'))).toBe(false)
  })

  it('uses the user-scoped personal endpoint for opted-in authenticated users', async () => {
    const personalExposure = { ...exposure, rankingVersion: 'similar-assets-v1-personal' }
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
      }
      if (url.includes('/api/account/assets/')) {
        expect(url).toContain(`/api/account/assets/${sourceId}/similar?`)
        return new Response(similarPayload([peer], personalExposure), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      throw new Error(`Unexpected request ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some((call) => String(call[0]).includes('/api/account/assets/')),
      ).toBe(true)
    })
  })

  it('fires no similar request while auth is still loading', async () => {
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL): Promise<Response> =>
        new Promise<Response>(() => {
          /* session and everything else stay pending */
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { loadSession: true })

    expect(screen.getByLabelText(/loading similar assets/i)).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('/similar')),
    ).toHaveLength(0)
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('recommendation-events')),
    ).toHaveLength(0)
  })

  it('fires no similar request while authenticated preferences are still pending', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return new Promise<Response>(() => {
          /* preferences stay pending */
        })
      }
      return new Response(similarPayload([peer]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    expect(screen.getByLabelText(/loading similar assets/i)).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('/similar')),
    ).toHaveLength(0)
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('recommendation-events')),
    ).toHaveLength(0)
  })

  it('fires exactly the personal query once preferences resolve to opted-in', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
      }
      if (url.includes('/api/account/assets/')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      throw new Error(`Unexpected request ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter((call) => String(call[0]).includes('/api/account/assets/')),
      ).toHaveLength(1)
    })
    expect(
      fetchMock.mock.calls.filter(
        (call) =>
          String(call[0]).includes('/api/assets/') &&
          !String(call[0]).includes('/api/account/assets/'),
      ),
    ).toHaveLength(0)
  })

  it('fires exactly the public query once preferences resolve to opted-out', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: false, optedInAt: null })
      }
      if (url.includes('/api/assets/') && !url.includes('/api/account/assets/')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      throw new Error(`Unexpected request ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter(
          (call) =>
            String(call[0]).includes('/api/assets/') &&
            !String(call[0]).includes('/api/account/assets/'),
        ),
      ).toHaveLength(1)
    })
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('/api/account/assets/')),
    ).toHaveLength(0)
  })

  it('falls back to the public query when preferences fail, without personal requests', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return new Response('{"title":"fail"}', { status: 500 })
      }
      if (url.includes('/api/assets/') && !url.includes('/api/account/assets/')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      throw new Error(`Unexpected request ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    // Documented safe fallback: content stays available via public non-personal order while
    // personalization stays off; the error is never recorded as a saved opt-out.
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('/api/account/assets/')),
    ).toHaveLength(0)
  })

  it('deactivates the personal query when a preferences refetch fails after opt-in', async () => {
    const user = userEvent.setup()
    const personalExposure = {
      ...exposure,
      id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
      rankingVersion: 'similar-assets-v1-personal',
      token: 'b'.repeat(64),
    }
    let preferencesCalls = 0
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        preferencesCalls += 1
        if (preferencesCalls === 1) {
          return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
        }
        return new Response('{"title":"fail"}', { status: 500 })
      }
      if (url.includes('/api/account/assets/')) {
        return new Response(similarPayload([peer], personalExposure), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (url.includes('/api/assets/')) {
        return new Response(similarPayload([peer]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(null, { status: 202 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const { queryClient } = renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, {
      authUser,
    })

    // Opted in: exactly the personal query fires.
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter((call) => String(call[0]).includes('/api/account/assets/')),
      ).toHaveLength(1)
    })

    // Failed refetch must deactivate personal despite stale cached opt-in data.
    await queryClient.invalidateQueries({ queryKey: accountKeys.recommendationPreferences() })
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter(
          (call) =>
            String(call[0]).includes('/api/assets/') &&
            !String(call[0]).includes('/api/account/assets/'),
        ),
      ).toHaveLength(1)
    })

    // Mode change/refetch uses only the public route from here on.
    await user.click(screen.getByRole('button', { name: /popular/i }))
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter(
          (call) =>
            String(call[0]).includes('/api/assets/') &&
            !String(call[0]).includes('/api/account/assets/'),
        ),
      ).toHaveLength(2)
    })
    expect(
      fetchMock.mock.calls.filter((call) => String(call[0]).includes('/api/account/assets/')),
    ).toHaveLength(1)

    // Impressions after the failure carry the public exposure, never the personal one.
    vi.useFakeTimers()
    FakeIntersectionObserver.instance!.trigger(true, 0.5)
    await vi.advanceTimersByTimeAsync(1000)

    const recCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('recommendation-events'),
    )
    expect(recCalls.length).toBeGreaterThan(0)
    for (const call of recCalls) {
      const body = JSON.parse(String(call[1]?.body)) as {
        exposureId: string
        exposureToken: string
      }
      expect(body.exposureId).toBe(exposure.id)
      expect(body.exposureToken).toBe(exposure.token)
    }
  })

  it('renders one explanation per card from the same response', async () => {
    const explanations = [
      {
        assetId: peer.id,
        code: 'SHARED_TAGS',
        text: 'Shares 1 tag with this asset.',
      },
    ]
    const fetchMock = vi.fn(async () => {
      return new Response(JSON.stringify({ items: [peer], exposure, explanations }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)

    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    expect(await screen.findByText('Shares 1 tag with this asset.')).toBeInTheDocument()
  })

  it('shows explanations from the matching cached response on mode and personal switches', async () => {
    const user = userEvent.setup()
    const similarityText = 'Shares 2 tags with this asset.'
    const popularityText = 'Popular in this category.'
    const personalText = 'Based on your recommendation choices.'
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
      }
      if (url.includes('/api/account/assets/')) {
        const text = url.includes('mode=popularity') ? popularityText : personalText
        const code = url.includes('mode=popularity')
          ? 'POPULAR_IN_CATEGORY'
          : 'PERSONAL_RECOMMENDATION_CHOICES'
        return new Response(
          JSON.stringify({
            items: [peer],
            exposure,
            explanations: [{ assetId: peer.id, code, text }],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      if (url.includes('/api/assets/')) {
        const text = url.includes('mode=popularity') ? popularityText : similarityText
        const code = url.includes('mode=popularity') ? 'POPULAR_IN_CATEGORY' : 'SHARED_TAGS'
        return new Response(
          JSON.stringify({
            items: [peer],
            exposure,
            explanations: [{ assetId: peer.id, code, text }],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      throw new Error(`Unexpected request ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<SimilarAssetsBlock assetId={sourceId} />, { authUser })

    // Opted in: personal response text without an extra request.
    expect(await screen.findByText('Peer Pack')).toBeInTheDocument()
    expect(await screen.findByText(personalText)).toBeInTheDocument()

    // Mode switch: popularity response text.
    await user.click(screen.getByRole('button', { name: /popular/i }))
    expect(await screen.findByText(popularityText)).toBeInTheDocument()
    expect(screen.queryByText(personalText)).not.toBeInTheDocument()
  })

  it('renders explanation and catalog text as inert text, never as HTML', async () => {
    const evilPeer = {
      ...peer,
      title: '<img src=x onerror=alert(1)>Evil Pack',
      description: '<script>alert(2)</script>',
      tags: ['<b>bold</b>'],
    }
    const evilText = '<img src=x onerror=alert(3)>Shares 1 tag with this asset.'
    const fetchMock = vi.fn(async () => {
      return new Response(
        JSON.stringify({
          items: [evilPeer],
          exposure,
          explanations: [{ assetId: evilPeer.id, code: 'SHARED_TAGS', text: evilText }],
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    const { container } = renderWithQueryClient(<SimilarAssetsBlock assetId={sourceId} />)

    // Literal text is visible...
    expect(await screen.findByText(evilText)).toBeInTheDocument()
    expect(await screen.findByText('<img src=x onerror=alert(1)>Evil Pack')).toBeInTheDocument()
    // ...but nothing executes or parses as markup.
    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('b')).toBeNull()
  })
})
