import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { SignInForm } from '@/components/auth/sign-in-form'
import { authKeys } from '@/lib/auth/auth-query'
import { renderWithProviders, renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

const navigation = vi.hoisted(() => ({
  push: vi.fn(),
  refresh: vi.fn(),
  search: '',
}))

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: navigation.push, refresh: navigation.refresh }),
  useSearchParams: () => new URLSearchParams(navigation.search),
}))

vi.mock('sonner', () => ({ toast: { success: vi.fn() } }))

afterEach(() => {
  navigation.push.mockReset()
  navigation.refresh.mockReset()
  navigation.search = ''
})

async function fillAndSubmit() {
  const user = userEvent.setup()
  await user.type(screen.getByLabelText('Email'), 'seller@example.com')
  await user.type(screen.getByLabelText('Password'), 'correct horse battery staple')
  await user.click(screen.getByRole('button', { name: 'Sign in' }))
}

describe('SignInForm', () => {
  it('submits valid credentials, disables while pending, and refreshes session state', async () => {
    const sessionUser = verifiedSeller()
    let resolveLogin: (() => void) | undefined
    const loginPending = new Promise<void>((resolve) => {
      resolveLogin = resolve
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url === '/api/auth/login') {
          await loginPending
          return Response.json({})
        }
        if (url === '/api/auth/session') return Response.json({ user: sessionUser })
        if (url === '/api/account/me') return Response.json(sessionUser)
        throw new Error(`Unexpected request ${url}`)
      }),
    )

    const { queryClient } = renderWithProviders(<SignInForm />, { authUser: null })
    await fillAndSubmit()
    expect(screen.getByRole('button', { name: 'Signing in…' })).toBeDisabled()
    resolveLogin?.()

    await waitFor(() => expect(navigation.push).toHaveBeenCalledWith('/assets'))
    expect(queryClient.getQueryData(authKeys.session())).toEqual(sessionUser)
    expect(navigation.refresh).toHaveBeenCalledOnce()
  })

  it('renders safe ProblemDetails credentials failure without optimistic navigation', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Response.json(
          { title: 'Invalid credentials', detail: 'Email or password is incorrect.' },
          { status: 401 },
        ),
      ),
    )
    renderWithQueryClient(<SignInForm />)
    await fillAndSubmit()

    expect(await screen.findByText('Email or password is incorrect.')).toBeInTheDocument()
    expect(navigation.push).not.toHaveBeenCalled()
  })

  it('blocks invalid form values before network submission', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<SignInForm />)
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Email is required')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rejects external and protocol-relative return URLs', async () => {
    navigation.search = 'returnUrl=https%3A%2F%2Fevil.example%2Fsteal'
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url === '/api/auth/login') return Response.json({})
        if (url === '/api/auth/session') return Response.json({ user: verifiedSeller() })
        if (url === '/api/account/me') return Response.json(verifiedSeller())
        throw new Error(`Unexpected request ${url}`)
      }),
    )
    renderWithQueryClient(<SignInForm />)
    await fillAndSubmit()

    await waitFor(() => expect(navigation.push).toHaveBeenCalledWith('/assets'))
  })
})
