import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { useQuery } from '@tanstack/react-query'
import { renderWithProviders, renderWithQueryClient } from '@/test/render'
import { useAuth } from '@/components/auth/auth-context'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

function DummyQueryComponent() {
  const query = useQuery({
    queryKey: ['dummy-test-key'],
    queryFn: () => 'data-from-query',
  })
  return <div>{query.data ?? 'loading'}</div>
}

function DummyAuthComponent() {
  const { user, status, isAdmin } = useAuth()
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="username">{user?.username ?? 'none'}</span>
      <span data-testid="is-admin">{isAdmin ? 'admin' : 'regular'}</span>
    </div>
  )
}

describe('test render infrastructure', () => {
  it('renders with fresh QueryClient via renderWithQueryClient', async () => {
    const { queryClient } = renderWithQueryClient(<DummyQueryComponent />)
    expect(queryClient).toBeDefined()
    expect(await screen.findByText('data-from-query')).toBeInTheDocument()
  })

  it('renders with supplied custom QueryClient via renderWithQueryClient', () => {
    const customClient = createTestQueryClient()
    customClient.setQueryData(['dummy-test-key'], 'preloaded-custom-data')

    const { queryClient } = renderWithQueryClient(<DummyQueryComponent />, {
      queryClient: customClient,
    })

    expect(queryClient).toBe(customClient)
    expect(screen.getByText('preloaded-custom-data')).toBeInTheDocument()
  })

  it('renders with anonymous user state via renderWithProviders', () => {
    const { queryClient } = renderWithProviders(<DummyAuthComponent />, {
      authUser: null,
    })

    expect(queryClient).toBeDefined()
    expect(screen.getByTestId('status').textContent).toBe('anonymous')
    expect(screen.getByTestId('username').textContent).toBe('none')
    expect(screen.getByTestId('is-admin').textContent).toBe('regular')
  })

  it('renders with authenticated seller user state via renderWithProviders', () => {
    const testUser = verifiedSeller({
      id: '11111111-1111-4111-8111-111111111111',
      username: 'alice_seller',
      role: 'User',
    })

    renderWithProviders(<DummyAuthComponent />, {
      authUser: testUser,
    })

    expect(screen.getByTestId('status').textContent).toBe('authenticated')
    expect(screen.getByTestId('username').textContent).toBe('alice_seller')
    expect(screen.getByTestId('is-admin').textContent).toBe('regular')
  })

  it('renders with authenticated admin user state via renderWithProviders', () => {
    const adminUser = verifiedSeller({
      id: '22222222-2222-4222-8222-222222222222',
      username: 'admin_bob',
      role: 'Admin',
    })

    renderWithProviders(<DummyAuthComponent />, {
      authUser: adminUser,
    })

    expect(screen.getByTestId('status').textContent).toBe('authenticated')
    expect(screen.getByTestId('username').textContent).toBe('admin_bob')
    expect(screen.getByTestId('is-admin').textContent).toBe('admin')
  })

  it('defaults to anonymous without triggering network fetch when no options provided', () => {
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)

    renderWithProviders(<DummyAuthComponent />)

    expect(screen.getByTestId('status').textContent).toBe('anonymous')
    expect(screen.getByTestId('username').textContent).toBe('none')
    expect(fetchSpy).not.toHaveBeenCalled()
    vi.unstubAllGlobals()
  })
})
