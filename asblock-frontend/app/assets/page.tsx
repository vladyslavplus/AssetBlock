'use client'

import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { AssetCard } from '@/components/assets/asset-card'
import { AssetCardGridSkeleton } from '@/components/assets/asset-card-skeleton'
import { CatalogFiltersUI } from '@/components/assets/catalog-filters'
import { CatalogToolbar } from '@/components/assets/catalog-toolbar'
import { Button } from '@/components/ui/button'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { AlertCircle, ChevronLeft, ChevronRight } from 'lucide-react'
import {
  CATALOG_ASSETS_PAGE_SIZE,
  DEFAULT_CATALOG_FILTERS,
  sortDirectionForSortBy,
  type CatalogFilters,
} from '@/lib/catalog/catalog-filters'
import { catalogKeys, fetchCatalogFacets, fetchCatalogPage } from '@/lib/catalog/catalog-query'

export default function AssetsPage() {
  const [filters, setFilters] = useState<CatalogFilters>(DEFAULT_CATALOG_FILTERS)
  const [mobileFiltersOpen, setMobileFiltersOpen] = useState(false)

  const facetsQuery = useQuery({
    queryKey: catalogKeys.facets(),
    // Keep short public catalog reads alive across route transitions. Forwarding React Query's
    // cancellation signal to browser fetch causes Next dev to report an unhandled AbortError.
    queryFn: () => fetchCatalogFacets(),
    staleTime: 5 * 60 * 1000,
  })

  const listQuery = useQuery({
    queryKey: catalogKeys.list(filters),
    queryFn: () => fetchCatalogPage(filters),
    placeholderData: keepPreviousData,
  })

  const categories = facetsQuery.data?.categories ?? []
  const tags = facetsQuery.data?.tags ?? []
  const facetsLoading = facetsQuery.isPending
  const facetsError = facetsQuery.isError
    ? 'Could not load categories or tags. Check that the API is running.'
    : null

  const pageData = listQuery.data
  const listLoading = listQuery.isPending
  const listError = listQuery.isError
    ? listQuery.error instanceof Error
      ? listQuery.error.message
      : 'Could not load the catalog.'
    : null

  const handleFilterChange = (updates: Partial<CatalogFilters>) => {
    setFilters((prev) => {
      const next: CatalogFilters = { ...prev, ...updates }
      next.pageSize = CATALOG_ASSETS_PAGE_SIZE
      if (updates.sortBy !== undefined) {
        next.sortDirection = sortDirectionForSortBy(updates.sortBy)
      }
      const shouldResetPage =
        updates.page === undefined &&
        (updates.search !== undefined ||
          updates.categoryId !== undefined ||
          updates.tags !== undefined ||
          updates.minPrice !== undefined ||
          updates.maxPrice !== undefined)
      if (shouldResetPage) next.page = 1
      return next
    })
  }

  const handleResetFilters = () => {
    setFilters(DEFAULT_CATALOG_FILTERS)
  }

  const items = pageData?.items ?? []
  const totalCount = pageData?.totalCount ?? 0
  const totalPages = pageData?.totalPages ?? 0

  const hasActiveFilters =
    Boolean(filters.search?.trim()) ||
    Boolean(filters.categoryId) ||
    filters.tags.length > 0 ||
    filters.minPrice !== null ||
    filters.maxPrice !== null

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <SiteMain>
        <SitePageContainer variant="site">
          <div className="mb-8">
            <h1 className="text-3xl font-semibold text-balance">Browse assets</h1>
            <p className="mt-2 text-muted-foreground text-sm">
              Discover templates, tools, and code packages from the developer community.
            </p>
          </div>

          {facetsError && (
            <p className="mb-4 text-sm text-amber-600 dark:text-amber-400/90" role="status">
              {facetsError}
            </p>
          )}

          {listError && (
            <div className="mb-6 rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
              {listError}
            </div>
          )}

          <div className="hidden lg:grid grid-cols-[280px_1fr] gap-8">
            <aside className="sticky top-24">
              <div className="rounded-lg border border-border p-4 bg-card-elevated">
                <h2 className="font-semibold text-sm mb-4">Filters</h2>
                <CatalogFiltersUI
                  filters={filters}
                  onFilterChange={handleFilterChange}
                  onReset={handleResetFilters}
                  categories={categories}
                  tags={tags}
                  facetsLoading={facetsLoading}
                  isLoading={listLoading}
                />
              </div>
            </aside>

            <div className="space-y-4">
              <CatalogToolbar
                filters={filters}
                totalCount={totalCount}
                displayCount={items.length}
                onFilterChange={handleFilterChange}
                isDesktop={true}
                disabled={listLoading}
                isCountsLoading={listLoading && items.length === 0}
              />

              {hasActiveFilters && (
                <div className="flex flex-wrap gap-2">
                  {filters.categoryId && (
                    <div className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-primary/10 border border-primary/30">
                      <span className="text-xs text-foreground">
                        {categories.find((c) => c.id === filters.categoryId)?.name}
                      </span>
                      <button
                        type="button"
                        onClick={() => handleFilterChange({ categoryId: '' })}
                        className="text-primary hover:text-primary/80"
                      >
                        ×
                      </button>
                    </div>
                  )}
                  {filters.tags.map((tag) => (
                    <div
                      key={tag}
                      className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-primary/10 border border-primary/30"
                    >
                      <span className="text-xs text-foreground">{tag}</span>
                      <button
                        type="button"
                        onClick={() =>
                          handleFilterChange({
                            tags: filters.tags.filter((t) => t !== tag),
                          })
                        }
                        className="text-primary hover:text-primary/80"
                      >
                        ×
                      </button>
                    </div>
                  ))}
                  {filters.minPrice !== null && (
                    <div className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-primary/10 border border-primary/30">
                      <span className="text-xs text-foreground">Min: ${filters.minPrice}</span>
                      <button
                        type="button"
                        onClick={() => handleFilterChange({ minPrice: null })}
                        className="text-primary hover:text-primary/80"
                      >
                        ×
                      </button>
                    </div>
                  )}
                  {filters.maxPrice !== null && (
                    <div className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-primary/10 border border-primary/30">
                      <span className="text-xs text-foreground">Max: ${filters.maxPrice}</span>
                      <button
                        type="button"
                        onClick={() => handleFilterChange({ maxPrice: null })}
                        className="text-primary hover:text-primary/80"
                      >
                        ×
                      </button>
                    </div>
                  )}
                </div>
              )}

              {listLoading && items.length === 0 ? (
                <AssetCardGridSkeleton variant="catalog-desktop" />
              ) : items.length > 0 ? (
                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                  {items.map((asset) => (
                    <AssetCard key={asset.id} asset={asset} />
                  ))}
                </div>
              ) : (
                <div className="rounded-lg border border-border/50 p-12 text-center">
                  <AlertCircle className="w-12 h-12 text-muted-foreground/30 mx-auto mb-4" />
                  <h3 className="font-semibold text-foreground mb-2">No assets found</h3>
                  <p className="text-sm text-muted-foreground mb-4">
                    Try adjusting your filters or search term.
                  </p>
                  <Button
                    type="button"
                    onClick={handleResetFilters}
                    className="bg-primary text-primary-foreground hover:bg-[#6D28D9] text-xs h-8"
                  >
                    Clear filters
                  </Button>
                </div>
              )}

              {totalPages > 1 && (
                <div className="flex items-center justify-center gap-2 pt-4">
                  <Button
                    type="button"
                    onClick={() => handleFilterChange({ page: Math.max(1, filters.page - 1) })}
                    disabled={filters.page === 1 || listLoading}
                    variant="outline"
                    size="sm"
                    className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground disabled:text-muted-foreground/50 disabled:hover:bg-transparent disabled:hover:border-border text-xs h-8 px-2"
                  >
                    <ChevronLeft className="w-4 h-4" />
                  </Button>
                  <span className="text-xs text-muted-foreground">
                    Page {filters.page} of {totalPages}
                  </span>
                  <Button
                    type="button"
                    onClick={() =>
                      handleFilterChange({ page: Math.min(totalPages, filters.page + 1) })
                    }
                    disabled={filters.page === totalPages || listLoading}
                    variant="outline"
                    size="sm"
                    className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground disabled:text-muted-foreground/50 disabled:hover:bg-transparent disabled:hover:border-border text-xs h-8 px-2"
                  >
                    <ChevronRight className="w-4 h-4" />
                  </Button>
                </div>
              )}
            </div>
          </div>

          <div className="lg:hidden space-y-4">
            <CatalogToolbar
              filters={filters}
              totalCount={totalCount}
              displayCount={items.length}
              onFilterChange={handleFilterChange}
              onFilterClick={() => setMobileFiltersOpen(true)}
              isDesktop={false}
              disabled={listLoading}
              isCountsLoading={listLoading && items.length === 0}
            />

            {hasActiveFilters && (
              <div className="flex flex-wrap gap-2">
                {filters.categoryId && (
                  <div className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-primary/10 border border-primary/30">
                    <span className="text-xs text-foreground">
                      {categories.find((c) => c.id === filters.categoryId)?.name}
                    </span>
                    <button
                      type="button"
                      onClick={() => handleFilterChange({ categoryId: '' })}
                      className="text-primary hover:text-primary/80"
                    >
                      ×
                    </button>
                  </div>
                )}
                {filters.tags.map((tag) => (
                  <div
                    key={tag}
                    className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-primary/10 border border-primary/30"
                  >
                    <span className="text-xs text-foreground">{tag}</span>
                    <button
                      type="button"
                      onClick={() =>
                        handleFilterChange({
                          tags: filters.tags.filter((t) => t !== tag),
                        })
                      }
                      className="text-primary hover:text-primary/80"
                    >
                      ×
                    </button>
                  </div>
                ))}
              </div>
            )}

            <Sheet open={mobileFiltersOpen} onOpenChange={setMobileFiltersOpen}>
              <SheetContent side="left" className="w-[280px] bg-card-elevated border-border p-4">
                <SheetHeader>
                  <SheetTitle className="text-base">Filters</SheetTitle>
                  <SheetDescription className="text-xs">Narrow down your search</SheetDescription>
                </SheetHeader>
                <div className="mt-4">
                  <CatalogFiltersUI
                    filters={filters}
                    onFilterChange={handleFilterChange}
                    onReset={handleResetFilters}
                    categories={categories}
                    tags={tags}
                    facetsLoading={facetsLoading}
                    isLoading={listLoading}
                  />
                </div>
              </SheetContent>
            </Sheet>

            {listLoading && items.length === 0 ? (
              <AssetCardGridSkeleton variant="catalog-mobile" />
            ) : items.length > 0 ? (
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                {items.map((asset) => (
                  <AssetCard key={asset.id} asset={asset} />
                ))}
              </div>
            ) : (
              <div className="rounded-lg border border-border/50 p-12 text-center">
                <AlertCircle className="w-12 h-12 text-muted-foreground/30 mx-auto mb-4" />
                <h3 className="font-semibold text-foreground mb-2">No assets found</h3>
                <p className="text-sm text-muted-foreground mb-4">
                  Try adjusting your filters or search term.
                </p>
                <Button
                  type="button"
                  onClick={handleResetFilters}
                  className="bg-primary text-primary-foreground hover:bg-[#6D28D9] text-xs h-8"
                >
                  Clear filters
                </Button>
              </div>
            )}

            {totalPages > 1 && (
              <div className="flex items-center justify-center gap-2 pt-4">
                <Button
                  type="button"
                  onClick={() => handleFilterChange({ page: Math.max(1, filters.page - 1) })}
                  disabled={filters.page === 1 || listLoading}
                  variant="outline"
                  size="sm"
                  className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground disabled:text-muted-foreground/50 disabled:hover:bg-transparent disabled:hover:border-border text-xs h-8 px-2"
                >
                  <ChevronLeft className="w-4 h-4" />
                </Button>
                <span className="text-xs text-muted-foreground">
                  Page {filters.page} of {totalPages}
                </span>
                <Button
                  type="button"
                  onClick={() =>
                    handleFilterChange({ page: Math.min(totalPages, filters.page + 1) })
                  }
                  disabled={filters.page === totalPages || listLoading}
                  variant="outline"
                  size="sm"
                  className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground disabled:text-muted-foreground/50 disabled:hover:bg-transparent disabled:hover:border-border text-xs h-8 px-2"
                >
                  <ChevronRight className="w-4 h-4" />
                </Button>
              </div>
            )}
          </div>
        </SitePageContainer>
      </SiteMain>

      <SiteFooter />
    </div>
  )
}
