import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ModerationQueue } from './moderation-queue'
import { renderWithProviders } from '@/test/render'

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const otherId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const item = {
  submissionId: id,
  assetId: id,
  assetTitle: 'Queue asset',
  versionNumber: 2,
  submittedAt: '2026-11-01T10:00:00Z',
  state: 'SUBMITTED',
  kind: 'STANDARD',
  claimHolderId: null,
  claimHolderName: null,
  caseRevision: 1,
  coverageLevel: 'FULL',
  materialFindingCount: 1,
  hasOpenResponses: false,
}
const user = {
  id: otherId,
  username: 'moderator',
  role: 'Moderator',
  roleRevision: 1,
  emailVerifiedAt: '2026-01-01T00:00:00Z',
  avatarUrl: null,
  bio: null,
  isPublicProfile: false,
  createdAt: '2026-01-01T00:00:00Z',
  socialLinks: [],
}

function queueResponse(items: unknown[]) {
  return new Response(JSON.stringify({ items, nextCursor: null }), {
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('moderation queue', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows loading, empty and error states', async () => {
    let finish: ((response: Response) => void) | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            finish = resolve
          }),
      ),
    )
    const { unmount } = renderWithProviders(<ModerationQueue />, { authUser: user })
    expect(screen.getByRole('status')).toHaveTextContent('Loading moderation queue')
    finish?.(queueResponse([]))
    await screen.findByText('No cases in this queue')
    unmount()

    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('Unavailable', { status: 503 })),
    )
    renderWithProviders(<ModerationQueue />, { authUser: user })
    expect(
      await screen.findByRole('heading', { name: 'Could not load the queue' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Moderation queue failed (503)')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })

  it('shows current reviewer as claim holder and releases owned claim', async () => {
    const claimed = {
      ...item,
      state: 'IN_REVIEW',
      claimHolderId: otherId,
      claimHolderName: 'moderator',
    }
    const fetchMock = vi.fn(async (_url: string, init?: RequestInit) =>
      init?.method === 'POST'
        ? new Response(
            JSON.stringify({
              submissionId: id,
              state: 'SUBMITTED',
              holderId: null,
              caseRevision: 2,
              replayed: false,
            }),
            { headers: { 'Content-Type': 'application/json' } },
          )
        : queueResponse([claimed]),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationQueue />, { authUser: user })
    expect(await screen.findByText('You')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Release Queue asset' }))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(true),
    )
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter((call) => call[1]?.method !== 'POST')).toHaveLength(2),
    )
    expect(
      JSON.parse(
        fetchMock.mock.calls.find((call) => call[1]?.method === 'POST')?.[1]?.body as string,
      ),
    ).toMatchObject({
      expectedCaseRevision: 1,
    })
  })

  it('refreshes queue after a claim conflict and reports forbidden writes', async () => {
    let writes = 0
    const fetchMock = vi.fn(async (_url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        writes += 1
        return new Response(
          JSON.stringify({ code: writes === 1 ? 'ERR_MODERATION_CASE_HELD' : 'ERR_FORBIDDEN' }),
          {
            status: writes === 1 ? 409 : 403,
            headers: { 'Content-Type': 'application/problem+json' },
          },
        )
      }
      return queueResponse([item])
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationQueue />, { authUser: user })
    fireEvent.click(await screen.findByRole('button', { name: 'Claim Queue asset' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Claim state changed. Refresh the queue before trying again.',
    )
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter((call) => call[1]?.method !== 'POST')).toHaveLength(2),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Claim Queue asset' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'You are not permitted to claim or release this case',
    )
  })

  it('loads the next keyset page only on request', async () => {
    const fetchMock = vi.fn(async (url: string) =>
      url.includes('cursor=next-page')
        ? queueResponse([{ ...item, submissionId: otherId, assetTitle: 'Second asset' }])
        : new Response(JSON.stringify({ items: [item], nextCursor: 'next-page' }), {
            headers: { 'Content-Type': 'application/json' },
          }),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<ModerationQueue />, { authUser: user })
    expect(await screen.findByText('Queue asset')).toBeInTheDocument()
    expect(screen.queryByText('Second asset')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }))
    expect(await screen.findByText('Second asset')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('shows an accessible text badge for appeal cases', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => queueResponse([{ ...item, kind: 'APPEAL' }])),
    )
    renderWithProviders(<ModerationQueue />, { authUser: user })
    expect(await screen.findByText('Appeal')).toBeInTheDocument()
    expect(screen.getByRole('row', { name: /Queue asset Appeal/ })).toBeInTheDocument()
  })
})
