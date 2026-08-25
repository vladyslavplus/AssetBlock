import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { NotificationBell } from '@/components/notifications/notification-bell'
import { notificationsKeys } from '@/lib/notifications/notifications-query'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

const subscribeNotificationHub = vi.hoisted(() => vi.fn())
const useAuth = vi.hoisted(() => vi.fn())

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('@/lib/notifications/notification-hub', () => ({
  subscribeNotificationHub: (cb: () => void, userId: string) =>
    subscribeNotificationHub(cb, userId),
}))

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }))

describe('NotificationBell hub invalidation', () => {
  it('invalidates only notification queries when the hub fires', async () => {
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    let hubCb: (() => void) | undefined
    subscribeNotificationHub.mockImplementation((cb: () => void) => {
      hubCb = cb
      return () => {}
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () => new Response(JSON.stringify({ items: [], totalCount: 0 }), { status: 200 }),
      ),
    )
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    render(
      <QueryClientProvider client={queryClient}>
        <NotificationBell />
      </QueryClientProvider>,
    )
    await screen.findByRole('button', { name: /notifications/i })
    expect(subscribeNotificationHub).toHaveBeenCalledWith(expect.any(Function), verifiedSeller().id)
    hubCb?.()
    expect(invalidate).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: notificationsKeys.all }),
      expect.anything(),
    )
    const keys = invalidate.mock.calls.map((call) => JSON.stringify(call[0]))
    expect(keys.some((key) => key.includes('catalog') || key.includes('library'))).toBe(false)
  })
})
