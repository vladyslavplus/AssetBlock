import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { AccountSettingsForm } from '@/components/account/account-settings-form'
import { accountKeys } from '@/lib/account/account-query'
import { renderWithQueryClient } from '@/test/render'

const navigation = vi.hoisted(() => ({ push: vi.fn(), refresh: vi.fn() }))
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('next/navigation', () => ({ useRouter: () => navigation }))
vi.mock('sonner', () => ({ toast }))

const profile = {
  id: '11111111-1111-4111-8111-111111111111',
  username: 'seller',
  email: 'seller@example.com',
  role: 'User',
  avatarUrl: null,
  bio: null,
  isPublicProfile: true,
  createdAt: '2026-01-01T00:00:00.000Z',
  emailVerifiedAt: '2026-01-01T00:00:00.000Z',
  pendingEmail: null,
  pendingEmailChangeExpiresAt: null,
  socialLinks: [],
}

function setupFetch(patchResponse: Response) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    if (url === '/api/account/me' && init?.method === 'PATCH') return patchResponse
    if (url === '/api/account/me') return Response.json(profile)
    if (url === '/api/account/social-platforms') return Response.json([])
    if (url === '/api/account/recommendation-preferences' && init?.method === 'PATCH') {
      return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
    }
    if (url === '/api/account/recommendation-preferences') {
      return Response.json({ isPersonalized: false, optedInAt: null })
    }
    throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  navigation.push.mockReset()
  navigation.refresh.mockReset()
  toast.success.mockReset()
  toast.error.mockReset()
})

describe('AccountSettingsForm', () => {
  it('validates profile schema before mutation', async () => {
    const fetchMock = setupFetch(Response.json({}))
    renderWithQueryClient(<AccountSettingsForm />)
    const username = await screen.findByLabelText('Username')
    await userEvent.clear(username)
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Username is required')).toBeInTheDocument()
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PATCH')).toHaveLength(0)
  })

  it('updates cached profile only after backend success and exposes pending state', async () => {
    let resolvePatch: ((response: Response) => void) | undefined
    const pendingPatch = new Promise<Response>((resolve) => {
      resolvePatch = resolve
    })
    const fetchMock = setupFetch(Response.json({}))
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me' && init?.method === 'PATCH') return pendingPatch
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: false, optedInAt: null })
      }
      throw new Error(`Unexpected request ${url}`)
    })
    const { queryClient } = renderWithQueryClient(<AccountSettingsForm />)
    const username = await screen.findByLabelText('Username')
    await userEvent.clear(username)
    await userEvent.type(username, 'updated-seller')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(screen.getByRole('button', { name: 'Saving…' })).toBeDisabled()

    resolvePatch?.(
      Response.json({
        username: 'updated-seller',
        avatarUrl: null,
        bio: null,
        isPublicProfile: true,
      }),
    )
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Changes saved.'))
    expect(queryClient.getQueryData(accountKeys.me())).toMatchObject({ username: 'updated-seller' })
    expect(navigation.refresh).toHaveBeenCalledOnce()
  })

  it('keeps cached profile unchanged and does not show success after backend failure', async () => {
    setupFetch(
      Response.json({ title: 'Conflict', detail: 'Username is already taken.' }, { status: 409 }),
    )
    const { queryClient } = renderWithQueryClient(<AccountSettingsForm />)
    const username = await screen.findByLabelText('Username')
    await userEvent.clear(username)
    await userEvent.type(username, 'taken')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Username is already taken.'))
    expect(queryClient.getQueryData(accountKeys.me())).toMatchObject({ username: 'seller' })
    expect(toast.success).not.toHaveBeenCalled()
    expect(navigation.refresh).not.toHaveBeenCalled()
  })

  it('renders personalized recommendations off by default', async () => {
    setupFetch(Response.json({}))
    renderWithQueryClient(<AccountSettingsForm />)

    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    expect(toggle).toHaveAttribute('aria-checked', 'false')
  })

  it('saves an opt-in through the profile save flow', async () => {
    const fetchMock = setupFetch(Response.json({}))
    const { queryClient } = renderWithQueryClient(<AccountSettingsForm />)

    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    await userEvent.click(toggle)
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Changes saved.'))
    const patches = fetchMock.mock.calls.filter(
      ([url, init]) =>
        url === '/api/account/recommendation-preferences' && init?.method === 'PATCH',
    )
    expect(patches).toHaveLength(1)
    expect(JSON.parse(String(patches[0]?.[1]?.body))).toEqual({ isPersonalized: true })
    expect(queryClient.getQueryData(accountKeys.recommendationPreferences())).toMatchObject({
      isPersonalized: true,
    })
  })

  it('drops user-scoped personal similar cache on opt-out save', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences' && init?.method === 'PATCH') {
        return Response.json({ isPersonalized: false, optedInAt: null })
      }
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
    const { queryClient } = renderWithQueryClient(<AccountSettingsForm />)
    const personalKey = [
      'assets',
      'similar',
      'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      6,
      'similarity',
      'personal',
      '11111111-1111-4111-8111-111111111111',
    ] as const
    queryClient.setQueryData(personalKey, { items: [] })

    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    await waitFor(() => expect(toggle).toHaveAttribute('aria-checked', 'true'))
    await userEvent.click(toggle)
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Changes saved.'))
    expect(queryClient.getQueryData(personalKey)).toBeUndefined()
  })

  it('disables the switch and shows error plus retry when preferences fail to load', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences') {
        return new Response('{"title":"fail"}', { status: 500 })
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AccountSettingsForm />)

    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    expect(toggle).toBeDisabled()
    expect(await screen.findByRole('alert')).toHaveTextContent('fail')
    const retry = screen.getByRole('button', { name: 'Retry' })
    expect(retry).toBeInTheDocument()
    // A fetch error is not a saved opt-out: no PATCH fires from the error state.
    expect(
      fetchMock.mock.calls.filter(
        ([url, init]) =>
          url === '/api/account/recommendation-preferences' && init?.method === 'PATCH',
      ),
    ).toHaveLength(0)
  })

  it('recovers the switch from a failed load after retry succeeds', async () => {
    let failPreferences = true
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences') {
        if (failPreferences) return new Response('{"title":"fail"}', { status: 500 })
        return Response.json({ isPersonalized: true, optedInAt: '2026-09-14T00:00:00.000Z' })
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AccountSettingsForm />)

    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    expect(toggle).toBeDisabled()
    const retry = await screen.findByRole('button', { name: 'Retry' })
    expect(retry).toBeInTheDocument()
    failPreferences = false
    await userEvent.click(retry)

    await waitFor(() => expect(toggle).toHaveAttribute('aria-checked', 'true'))
    expect(toggle).not.toBeDisabled()
  })

  it('treats a malformed preferences response as a load error, not opt-out', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences') {
        return Response.json({ isPersonalized: 'yes' })
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AccountSettingsForm />)

    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    expect(toggle).toBeDisabled()
    expect(await screen.findByText('Unexpected response from server.')).toBeInTheDocument()
    expect(
      fetchMock.mock.calls.filter(
        ([url, init]) =>
          url === '/api/account/recommendation-preferences' && init?.method === 'PATCH',
      ),
    ).toHaveLength(0)
  })

  it('redirects to login when preferences return 401', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences') {
        return new Response(null, { status: 401 })
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AccountSettingsForm />)

    await waitFor(() => expect(navigation.push).toHaveBeenCalledWith('/login?returnUrl=%2Faccount'))
  })

  it('never PATCHes preferences without a known server baseline', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url === '/api/account/me') return Response.json(profile)
      if (url === '/api/account/social-platforms') return Response.json([])
      if (url === '/api/account/recommendation-preferences') {
        return new Response('{"title":"fail"}', { status: 500 })
      }
      if (url === '/api/account/me' && init?.method === 'PATCH') {
        return Response.json(profile)
      }
      throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AccountSettingsForm />)

    // Baseline unknown: switch disabled, so only profile dirt marks the form dirty.
    const toggle = await screen.findByRole('switch', { name: 'Personalized recommendations' })
    expect(toggle).toBeDisabled()
    const username = await screen.findByLabelText('Username')
    await userEvent.clear(username)
    await userEvent.type(username, 'updated-seller')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Changes saved.'))
    expect(
      fetchMock.mock.calls.filter(
        ([url, init]) =>
          url === '/api/account/recommendation-preferences' && init?.method === 'PATCH',
      ),
    ).toHaveLength(0)
  })
})
