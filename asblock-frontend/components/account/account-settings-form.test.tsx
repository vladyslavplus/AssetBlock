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
})
