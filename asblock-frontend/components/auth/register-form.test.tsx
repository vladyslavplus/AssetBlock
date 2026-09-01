import { afterEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RegisterForm } from '@/components/auth/register-form'
import { renderWithProviders } from '@/test/render'

const pushMock = vi.fn()

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: pushMock }),
}))

describe('RegisterForm', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.clearAllMocks()
  })

  it('redirects to sign in after the indistinguishable accepted response', async () => {
    const fetchMock = vi.fn(async () => Response.json({ ok: true }, { status: 202 }))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    renderWithProviders(<RegisterForm />)

    await user.type(screen.getByLabelText('Username'), 'new-user')
    await user.type(screen.getByLabelText('Email address'), 'new@example.com')
    await user.type(screen.getByLabelText('Password'), 'Password1!')
    await user.type(screen.getByLabelText('Confirm password'), 'Password1!')
    await user.click(screen.getByRole('button', { name: 'Create account' }))

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith('/login?message=registration-accepted')
    })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/register',
      expect.objectContaining({ method: 'POST' }),
    )
  })
})
