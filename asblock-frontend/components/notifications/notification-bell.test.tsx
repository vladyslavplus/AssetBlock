import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { NotificationBell } from '@/components/notifications/notification-bell'
import { notificationsKeys } from '@/lib/notifications/notifications-query'
import { moderationKeys } from '@/lib/moderation/moderation-query'
import { sellerKeys } from '@/lib/seller/seller-query'
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
  it('invalidates unread count always and avoids inbox invalidation when closed', async () => {
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
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      expect(init?.signal).toBeUndefined()
      return new Response(JSON.stringify({ items: [], totalCount: 0 }), { status: 200 })
    })
    vi.stubGlobal('fetch', fetchMock)
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    render(
      <QueryClientProvider client={queryClient}>
        <NotificationBell />
      </QueryClientProvider>,
    )
    await screen.findByRole('button', { name: /notifications/i })
    expect(fetchMock).toHaveBeenCalled()
    expect(subscribeNotificationHub).toHaveBeenCalledWith(expect.any(Function), verifiedSeller().id)

    hubCb?.()

    const unreadKey = JSON.stringify(notificationsKeys.unread())
    const inboxKey = JSON.stringify(notificationsKeys.inbox())

    const calls = invalidate.mock.calls.map((call) => JSON.stringify(call[0]))
    expect(calls.some((k) => k.includes(unreadKey))).toBe(true)
    expect(calls.some((k) => k.includes(inboxKey))).toBe(false)
    expect(calls.some((k) => k === JSON.stringify({ queryKey: notificationsKeys.all }))).toBe(false)
    expect(calls.some((k) => k.includes('catalog') || k.includes('library'))).toBe(false)
  })

  it('invalidates both unread and inbox when the notification menu is open', async () => {
    const user = userEvent.setup()
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
    render(
      <QueryClientProvider client={queryClient}>
        <NotificationBell />
      </QueryClientProvider>,
    )
    const button = await screen.findByRole('button', { name: /notifications/i })
    await user.click(button)

    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    hubCb?.()

    const unreadKey = JSON.stringify(notificationsKeys.unread())
    const inboxKey = JSON.stringify(notificationsKeys.inbox())

    const calls = invalidate.mock.calls.map((call) => JSON.stringify(call[0]))
    expect(calls.some((k) => k.includes(unreadKey))).toBe(true)
    expect(calls.some((k) => k.includes(inboxKey))).toBe(true)
  })

  it('invalidates moderation case and seller review data when a case changes', async () => {
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    let hubCb: ((method: string, payload: unknown) => void) | undefined
    subscribeNotificationHub.mockImplementation(
      (cb: (method: string, payload: unknown) => void) => {
        hubCb = cb
        return () => {}
      },
    )
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
    hubCb?.('ModerationCaseChanged', {
      submissionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      versionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      state: 'APPROVED',
      revision: 3,
      isAppeal: false,
    })
    const calls = invalidate.mock.calls.map((call) => JSON.stringify(call[0]))
    expect(calls).toContain(JSON.stringify({ queryKey: moderationKeys.all }))
    expect(calls).toContain(
      JSON.stringify({
        queryKey: sellerKeys.versionReview(
          'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
          'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
        ),
      }),
    )
  })
})
