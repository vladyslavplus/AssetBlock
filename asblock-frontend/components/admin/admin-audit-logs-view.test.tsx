import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { AdminAuditLogsView } from '@/components/admin/admin-audit-logs-view'
import { renderWithQueryClient } from '@/test/render'

const row = {
  id: 1,
  occurredAt: '2026-08-30T12:00:00.000Z',
  actorType: 'USER',
  actorUserId: '11111111-1111-4111-8111-111111111111',
  action: 'Asset.Update',
  outcome: 'SUCCESS',
  resourceType: 'Asset',
  resourceId: '22222222-2222-4222-8222-222222222222',
  traceId: null,
  ipAddress: null,
  userAgent: null,
  metadata: null,
}

describe('AdminAuditLogsView', () => {
  it('applies filters explicitly and carries them into pagination queries', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL) =>
      Response.json({ items: [row], totalCount: 30, page: 1, pageSize: 20 }),
    )
    vi.stubGlobal('fetch', fetchMock)
    renderWithQueryClient(<AdminAuditLogsView />)
    await screen.findByText('Asset.Update')
    expect(fetchMock).toHaveBeenCalledOnce()

    await userEvent.type(screen.getByLabelText('Action'), 'Checkout')
    expect(fetchMock).toHaveBeenCalledOnce()
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(String(fetchMock.mock.calls[1]?.[0])).toContain('action=Checkout')
    expect(String(fetchMock.mock.calls[1]?.[0])).toContain('page=1')

    await userEvent.click(screen.getByRole('button', { name: 'Next' }))
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(String(fetchMock.mock.calls[2]?.[0])).toContain('action=Checkout')
    expect(String(fetchMock.mock.calls[2]?.[0])).toContain('page=2')
  })
})
