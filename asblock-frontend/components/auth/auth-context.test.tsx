import { QueryClientProvider, useQuery } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { AuthProvider, useAuth } from '@/components/auth/auth-context'
import { DEFAULT_CATALOG_FILTERS } from '@/lib/catalog/catalog-filters'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { authKeys } from '@/lib/auth/auth-query'
import type { SessionUser } from '@/lib/auth/auth-types'
import { sellerKeys } from '@/lib/seller/seller-query'
import { moderationKeys } from '@/lib/moderation/moderation-query'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

const userA = verifiedSeller()
const userB = verifiedSeller({
  id: '22222222-2222-4222-8222-222222222222',
  username: 'other',
})
const moderatorA = verifiedSeller({ role: 'Moderator', username: 'moderator-a' })
const excludedModeratorB = verifiedSeller({
  id: '22222222-2222-4222-8222-222222222222',
  role: 'Moderator',
  username: 'excluded-reviewer',
})

function AuthProbe() {
  const { user, status, refresh, logout } = useAuth()
  return (
    <div>
      <p>status:{status}</p>
      <p>user:{user?.username ?? 'none'}</p>
      <button type="button" onClick={() => void refresh()}>
        Refresh session
      </button>
      <button type="button" onClick={() => void logout()}>
        Sign out
      </button>
    </div>
  )
}

function renderAuth(queryClient: ReturnType<typeof createTestQueryClient>) {
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <AuthProbe />
      </AuthProvider>
    </QueryClientProvider>,
  )
}

function ModerationAuthProbe() {
  const { user } = useAuth()
  const query = useQuery({
    queryKey: moderationKeys.case(user?.id ?? '', 'appeal-case'),
    enabled: Boolean(user?.id),
    retry: false,
    queryFn: ({ signal }) => {
      window.dispatchEvent(new CustomEvent('moderation-fetch', { detail: signal }))
      return new Promise<{ privateText: string }>((resolve, reject) => {
        const finish = (event: Event) => {
          const result = (event as CustomEvent<{ resolve?: string; reject?: Error }>).detail
          if (result.reject) reject(result.reject)
          else resolve({ privateText: result.resolve ?? '' })
        }
        window.addEventListener(`moderation-result-${user?.id}`, finish, { once: true })
      })
    },
  })
  return <p>{query.data?.privateText ?? query.error?.message ?? 'No moderation case data'}</p>
}

function seedPrivateCache(
  queryClient: ReturnType<typeof createTestQueryClient>,
  user: SessionUser,
) {
  queryClient.setQueryData(authKeys.session(), user)
  queryClient.setQueryData(sellerKeys.listings(), { items: [`listings-for-${user.username}`] })
  queryClient.setQueryData(catalogKeys.list(DEFAULT_CATALOG_FILTERS), { items: ['public'] })
}

describe('AuthProvider session cache', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('clears private cache on session loss and keeps public catalog and session query', async () => {
    let session: SessionUser | null = userA
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/auth/session')) {
        return Response.json({ user: session })
      }
      throw new Error(`unexpected fetch ${url}`)
    })

    const queryClient = createTestQueryClient()
    seedPrivateCache(queryClient, userA)
    const user = userEvent.setup()
    renderAuth(queryClient)

    await screen.findByText('status:authenticated')
    session = null
    await user.click(screen.getByRole('button', { name: 'Refresh session' }))
    await screen.findByText('status:anonymous')

    expect(queryClient.getQueryData(sellerKeys.listings())).toBeUndefined()
    expect(queryClient.getQueryData(catalogKeys.list(DEFAULT_CATALOG_FILTERS))).toEqual({
      items: ['public'],
    })
    expect(queryClient.getQueryData(authKeys.session())).toBeNull()
  })

  it('clears private cache when the authenticated user switches', async () => {
    let session: SessionUser | null = userA
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/auth/session')) {
        return Response.json({ user: session })
      }
      throw new Error(`unexpected fetch ${url}`)
    })

    const queryClient = createTestQueryClient()
    seedPrivateCache(queryClient, userA)
    const user = userEvent.setup()
    renderAuth(queryClient)

    await screen.findByText('user:seller')
    session = userB
    await user.click(screen.getByRole('button', { name: 'Refresh session' }))
    await screen.findByText('user:other')

    expect(queryClient.getQueryData(sellerKeys.listings())).toBeUndefined()
    expect(queryClient.getQueryData(catalogKeys.list(DEFAULT_CATALOG_FILTERS))).toEqual({
      items: ['public'],
    })
    expect(queryClient.getQueryData(authKeys.session())).toEqual(userB)
  })

  it('clears moderation cache when the role changes for the same identity', async () => {
    let session: SessionUser | null = userA
    vi.stubGlobal('fetch', async (input: RequestInfo | URL) => {
      if (String(input).includes('/api/auth/session')) return Response.json({ user: session })
      throw new Error(`unexpected fetch ${String(input)}`)
    })
    const queryClient = createTestQueryClient()
    seedPrivateCache(queryClient, userA)
    const caseKey = moderationKeys.case(userA.id, 'case-1')
    queryClient.setQueryData(caseKey, { privateText: 'moderator-only' })
    const user = userEvent.setup()
    renderAuth(queryClient)

    await screen.findByText('user:seller')
    session = { ...userA, role: 'Moderator' }
    await user.click(screen.getByRole('button', { name: 'Refresh session' }))
    await vi.waitFor(() =>
      expect(queryClient.getQueryData<SessionUser>(authKeys.session())?.role).toBe('Moderator'),
    )
    await vi.waitFor(() => expect(queryClient.getQueryData(caseKey)).toBeUndefined())
    expect(queryClient.getQueryCache().findAll({ queryKey: moderationKeys.all })).toHaveLength(0)
  })

  it('clears private cache on logout without removing the session query', async () => {
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/api/auth/logout') && init?.method === 'POST') {
        return Response.json({ ok: true })
      }
      if (url.includes('/api/auth/session')) {
        return Response.json({ user: userA })
      }
      throw new Error(`unexpected fetch ${url}`)
    })

    const queryClient = createTestQueryClient()
    seedPrivateCache(queryClient, userA)
    const user = userEvent.setup()
    renderAuth(queryClient)

    await screen.findByText('status:authenticated')
    await user.click(screen.getByRole('button', { name: 'Sign out' }))
    await screen.findByText('status:anonymous')

    expect(queryClient.getQueryData(sellerKeys.listings())).toBeUndefined()
    expect(queryClient.getQueryData(catalogKeys.list(DEFAULT_CATALOG_FILTERS))).toEqual({
      items: ['public'],
    })
    expect(queryClient.getQueryData(authKeys.session())).toBeNull()
  })

  it('isolates retained moderation cache across logout and an excluded reviewer sign-in', async () => {
    let session: SessionUser | null = moderatorA
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/api/auth/logout') && init?.method === 'POST')
        return Response.json({ ok: true })
      if (url.includes('/api/auth/session')) return Response.json({ user: session })
      throw new Error(`unexpected fetch ${url}`)
    })

    const queryClient = createTestQueryClient()
    const caseKeyA = moderationKeys.case(moderatorA.id, 'appeal-case')
    queryClient.setQueryData(caseKeyA, { privateText: 'A_PRIVATE_APPEAL_RESPONSE' })
    queryClient.setQueryData(moderationKeys.queue(moderatorA.id, { pageSize: 25 }), {
      privateText: 'A_PRIVATE_QUEUE_PROVENANCE',
    })
    queryClient.setQueryData(moderationKeys.coverage(moderatorA.id), {
      privateText: 'A_PRIVATE_COVERAGE_NOTE',
    })
    queryClient.setQueryData(authKeys.session(), moderatorA)
    const user = userEvent.setup()
    const signals: AbortSignal[] = []
    const onFetch = (event: Event) => signals.push((event as CustomEvent<AbortSignal>).detail)
    window.addEventListener('moderation-fetch', onFetch)
    const rendered = render(
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <AuthProbe />
          <ModerationAuthProbe />
        </AuthProvider>
      </QueryClientProvider>,
    )

    await screen.findByText('A_PRIVATE_APPEAL_RESPONSE')
    await vi.waitFor(() => expect(signals.length).toBeGreaterThan(0))

    await user.click(screen.getByRole('button', { name: 'Sign out' }))
    await screen.findByText('No moderation case data')
    expect(signals.some((signal) => signal.aborted)).toBe(true)
    expect(queryClient.getQueryCache().findAll({ queryKey: moderationKeys.all })).toHaveLength(0)
    expect(queryClient.getQueryData(authKeys.session())).toBeNull()

    session = excludedModeratorB
    await user.click(screen.getByRole('button', { name: 'Refresh session' }))
    await screen.findByText('user:excluded-reviewer')
    const caseKeyB = moderationKeys.case(excludedModeratorB.id, 'appeal-case')
    let rejectB: ((error: Error) => void) | undefined
    let bSignal: AbortSignal | undefined
    const deniedRequest = queryClient.fetchQuery({
      queryKey: caseKeyB,
      queryFn: ({ signal }) => {
        bSignal = signal
        return new Promise<unknown>((_resolve, reject) => {
          rejectB = reject
        })
      },
    })
    await vi.waitFor(() => expect(rejectB).toBeDefined())
    expect(document.body.textContent).not.toContain('A_PRIVATE_')
    window.dispatchEvent(
      new CustomEvent(`moderation-result-${moderatorA.id}`, {
        detail: { resolve: 'A_LATE_PRIVATE_RESPONSE' },
      }),
    )
    rejectB?.(new Error('ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED'))
    await expect(deniedRequest).rejects.toThrow('ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED')

    expect(bSignal?.aborted).toBe(false)
    expect(document.body.textContent).not.toContain('A_PRIVATE_')
    expect(queryClient.getQueryData(caseKeyA)).toBeUndefined()
    expect(queryClient.getQueryData(caseKeyB)).toBeUndefined()
    expect(
      queryClient
        .getQueryCache()
        .findAll({ queryKey: moderationKeys.all })
        .map((query) => query.queryKey),
    ).not.toContainEqual(caseKeyA)
    window.removeEventListener('moderation-fetch', onFetch)
    rendered.unmount()
  })
})
