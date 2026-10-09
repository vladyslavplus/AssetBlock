import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { AdminUsersSection } from '@/components/admin/admin-users-section'
import { adminUserKeys } from '@/lib/admin/admin-users-query'
import { renderWithQueryClient } from '@/test/render'

const toast = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn(), info: vi.fn() }))

vi.mock('sonner', () => ({ toast }))

const UUID_USER = '11111111-1111-4111-8111-111111111111'
const UUID_ADMIN = '22222222-2222-4222-8222-222222222222'

function usersPayload() {
  return {
    items: [
      {
        id: UUID_USER,
        username: 'newmod',
        email: 'newmod@example.com',
        role: 'User',
        roleRevision: 4,
      },
      {
        id: UUID_ADMIN,
        username: 'root-admin',
        email: 'root@example.com',
        role: 'Admin',
        roleRevision: 1,
      },
    ],
    totalCount: 2,
    page: 1,
    pageSize: 20,
  }
}

describe('AdminUsersSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('lists users with roles and protects Admin accounts from role changes', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify(usersPayload()), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )
    renderWithQueryClient(<AdminUsersSection />)

    expect(await screen.findByText('newmod')).toBeInTheDocument()
    expect(screen.getByText('root-admin')).toBeInTheDocument()
    expect(screen.getByText('Protected')).toBeInTheDocument()
    expect(screen.queryByLabelText(/change role for root-admin/i)).not.toBeInTheDocument()
    expect(screen.getByLabelText(/change role for newmod/i)).toBeInTheDocument()
  })

  it('assigns Moderator with the expected role revision from the loaded row', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/role')) {
        return new Response(
          JSON.stringify({ userId: UUID_USER, role: 'Moderator', roleRevision: 5 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      return new Response(JSON.stringify(usersPayload()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AdminUsersSection />)

    await user.selectOptions(await screen.findByLabelText(/change role for newmod/i), 'Moderator')
    await user.click(screen.getByRole('button', { name: /^apply$/i }))

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        `/api/admin/users/${UUID_USER}/role`,
        expect.objectContaining({ method: 'PATCH' }),
      )
    })
    const roleCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/role'))
    expect(roleCall).toBeDefined()
    const init = (roleCall as unknown as [unknown, RequestInit])[1]
    const body = JSON.parse(String(init.body)) as Record<string, unknown>
    expect(body.role).toBe('Moderator')
    expect(body.expectedRoleRevision).toBe(4)
  })

  it('on a role-revision conflict refetches the list instead of granting optimistically', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/role')) {
        return new Response(
          JSON.stringify({
            type: 'urn:assetblock:error:ERR_ROLE_REVISION_STALE',
            code: 'ERR_ROLE_REVISION_STALE',
            title: 'Role conflict',
            detail: 'The user role changed. Refresh and try again.',
          }),
          { status: 409, headers: { 'Content-Type': 'application/json' } },
        )
      }
      return new Response(JSON.stringify(usersPayload()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })
    vi.stubGlobal('fetch', fetchMock)
    const { queryClient } = renderWithQueryClient(<AdminUsersSection />)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    await user.selectOptions(await screen.findByLabelText(/change role for newmod/i), 'Moderator')
    await user.click(screen.getByRole('button', { name: /^apply$/i }))

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith(
        expect.stringMatching(/this user was changed by someone else/i),
      )
    })
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: adminUserKeys.all }),
    )
  })
})
