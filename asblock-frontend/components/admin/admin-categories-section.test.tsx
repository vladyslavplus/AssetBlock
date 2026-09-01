import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { AdminCategoriesSection } from '@/components/admin/admin-categories-section'
import { adminKeys } from '@/lib/admin/admin-query'
import { renderWithQueryClient } from '@/test/render'

const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), message: vi.fn() }))
vi.mock('sonner', () => ({ toast }))

const category = {
  id: '11111111-1111-4111-8111-111111111111',
  name: 'Models',
  slug: 'models',
  description: 'Printable models',
}

function setup(deleteStatus = 204) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'DELETE') {
      return deleteStatus === 204
        ? new Response(null, { status: 204 })
        : Response.json({ detail: 'Category is still in use.' }, { status: deleteStatus })
    }
    return Response.json({ items: [category], totalCount: 1, page: 1, pageSize: 20 })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  toast.success.mockReset()
  toast.error.mockReset()
})

describe('AdminCategoriesSection destructive flow', () => {
  it('opens confirmation and cancel does not call delete', async () => {
    const fetchMock = setup()
    renderWithQueryClient(<AdminCategoriesSection />)
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Models' }))
    expect(screen.getByRole('alertdialog')).toHaveTextContent('Delete category?')
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(0)
  })

  it('confirms once and invalidates category query after success', async () => {
    const fetchMock = setup()
    const { queryClient } = renderWithQueryClient(<AdminCategoriesSection />)
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Models' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Category deleted.'))
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(1)
    expect(invalidateSpy).toHaveBeenCalledWith(
      { queryKey: adminKeys.categories() },
      { cancelRefetch: false },
    )
  })

  it('shows backend failure without success or closing into optimistic state', async () => {
    const fetchMock = setup(409)
    renderWithQueryClient(<AdminCategoriesSection />)
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Models' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Category is still in use.'))
    expect(toast.success).not.toHaveBeenCalled()
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(1)
  })
})
