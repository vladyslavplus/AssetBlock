import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { LibraryPageClient } from '@/components/library/library-page-client'
import { renderWithProviders } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'

vi.mock('@/components/site-header', () => ({ SiteHeader: () => null }))
vi.mock('@/components/site-footer', () => ({ SiteFooter: () => null }))

describe('LibraryPageClient', () => {
  it('renders empty state from an authenticated successful response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ items: [], totalCount: 0, page: 1, pageSize: 12 })),
    )
    renderWithProviders(<LibraryPageClient />, { authUser: verifiedSeller() })
    expect(await screen.findByText('No purchases yet')).toBeInTheDocument()
  })

  it('renders the loading state while the authenticated request is pending', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise<Response>(() => undefined)),
    )
    renderWithProviders(<LibraryPageClient />, { authUser: verifiedSeller() })

    expect(screen.getByLabelText('Loading library')).toHaveAttribute('aria-busy', 'true')
    expect(screen.queryByText('No purchases yet')).not.toBeInTheDocument()
  })

  it('renders backend failure and retry control without optimistic empty state', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ title: 'Service unavailable' }, { status: 503 })),
    )
    renderWithProviders(<LibraryPageClient />, { authUser: verifiedSeller() })
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load your library')
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled()
    expect(screen.queryByText('No purchases yet')).not.toBeInTheDocument()
  })

  it('does not request library data for a signed-out session', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    renderWithProviders(<LibraryPageClient />, { authUser: null })
    expect(screen.getByText('Sign in to view your library.')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
