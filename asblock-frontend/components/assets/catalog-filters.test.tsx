import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'

import { CatalogFiltersUI } from '@/components/assets/catalog-filters'
import { DEFAULT_CATALOG_FILTERS } from '@/lib/catalog/catalog-filters'
import { renderWithQueryClient } from '@/test/render'

describe('CatalogFiltersUI', () => {
  it('exposes a labeled search field', () => {
    renderWithQueryClient(
      <CatalogFiltersUI
        filters={DEFAULT_CATALOG_FILTERS}
        onFilterChange={vi.fn()}
        onReset={vi.fn()}
        categories={[{ id: 'c1', name: 'Code' }]}
        tags={['unity']}
      />,
    )
    expect(screen.getByLabelText(/search/i)).toBeInTheDocument()
  })

  it('offers the relevance sort option only while a search is active', async () => {
    const user = userEvent.setup()

    const onFilterChange = vi.fn(() => {})

    // No search yet: relevance mode is not an option.
    const { unmount } = renderWithQueryClient(
      <CatalogFiltersUI
        filters={DEFAULT_CATALOG_FILTERS}
        onFilterChange={onFilterChange}
        onReset={vi.fn()}
        categories={[{ id: 'c1', name: 'Code' }]}
        tags={['unity']}
      />,
    )
    await user.click(screen.getByRole('button', { name: /sort by/i }))
    expect(screen.queryByRole('menuitem', { name: /relevance/i })).not.toBeInTheDocument()

    unmount()

    // With a search term, relevance becomes available in the sort menu.
    renderWithQueryClient(
      <CatalogFiltersUI
        filters={{ ...DEFAULT_CATALOG_FILTERS, search: 'castle', sortBy: 'Relevance' }}
        onFilterChange={onFilterChange}
        onReset={vi.fn()}
        categories={[{ id: 'c1', name: 'Code' }]}
        tags={['unity']}
      />,
    )
    await user.click(screen.getByRole('button', { name: /sort by/i }))
    expect(screen.getByRole('menuitem', { name: /relevance/i })).toBeInTheDocument()
  })
})
