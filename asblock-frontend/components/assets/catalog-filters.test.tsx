import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { CatalogFiltersUI } from '@/components/assets/catalog-filters'
import { DEFAULT_CATALOG_FILTERS } from '@/lib/catalog/catalog-filters'
import { createTestQueryClient } from '@/test/query-client'

describe('CatalogFiltersUI', () => {
  it('exposes a labeled search field', () => {
    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <CatalogFiltersUI
          filters={DEFAULT_CATALOG_FILTERS}
          onFilterChange={vi.fn()}
          onReset={vi.fn()}
          categories={[{ id: 'c1', name: 'Code' }]}
          tags={['unity']}
        />
      </QueryClientProvider>,
    )
    expect(screen.getByLabelText(/search/i)).toBeInTheDocument()
  })
})
