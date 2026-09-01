import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

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
})
