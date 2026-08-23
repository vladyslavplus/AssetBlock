import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { AuthProvider, useAuth } from '@/components/auth/auth-context'
import { DEFAULT_CATALOG_FILTERS } from '@/lib/catalog/catalog-filters'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { authKeys } from '@/lib/auth/auth-query'
import type { SessionUser } from '@/lib/auth/auth-types'
import { sellerKeys } from '@/lib/seller/seller-query'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

const userA = verifiedSeller()
const userB = verifiedSeller({
  id: '22222222-2222-4222-8222-222222222222',
  username: 'other',
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
})
