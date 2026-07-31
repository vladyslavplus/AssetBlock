'use client'

import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { AlertCircle, ChevronLeft, ChevronRight } from 'lucide-react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { BundleCard } from '@/components/bundles/bundle-card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { bundleKeys, fetchBundlesListQuery } from '@/lib/bundles/bundles-query'

export function BundlesBrowsePage() {
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [appliedSearch, setAppliedSearch] = useState('')

  const filters = { page, search: appliedSearch }
  const listQuery = useQuery({
    queryKey: bundleKeys.publicList(filters),
    queryFn: () => fetchBundlesListQuery(filters),
    placeholderData: keepPreviousData,
  })

  const items = listQuery.data?.items ?? []
  const totalPages = listQuery.data?.totalPages ?? 0
  const loading = listQuery.isPending
  const bundleLinkSource = appliedSearch.trim() ? 'search' : 'catalog'
  const error = listQuery.isError
    ? listQuery.error instanceof Error
      ? listQuery.error.message
      : 'Could not load bundles.'
    : null

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="site">
          <div className="mb-8">
            <h1 className="text-3xl font-semibold text-balance">Bundles</h1>
            <p className="mt-2 text-muted-foreground text-sm">
              Discounted packs of assets from a single seller — one price, one checkout.
            </p>
          </div>

          <form
            className="mb-6 flex flex-col sm:flex-row gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              setPage(1)
              setAppliedSearch(search.trim())
            }}
          >
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search bundles…"
              className="bg-input border-border"
              aria-label="Search bundles"
            />
            <Button
              type="submit"
              className="bg-primary text-primary-foreground hover:bg-[#6D28D9] shrink-0"
            >
              Search
            </Button>
          </form>

          {error ? (
            <div className="mb-6 rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
              {error}
            </div>
          ) : null}

          {loading && items.length === 0 ? (
            <p className="text-sm text-muted-foreground py-8">Loading bundles…</p>
          ) : items.length > 0 ? (
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
              {items.map((b) => (
                <BundleCard key={b.id} bundle={b} linkSource={bundleLinkSource} />
              ))}
            </div>
          ) : (
            <div className="rounded-lg border border-border/50 p-12 text-center">
              <AlertCircle className="w-12 h-12 text-muted-foreground/30 mx-auto mb-4" />
              <h3 className="font-semibold text-foreground mb-2">No bundles found</h3>
              <p className="text-sm text-muted-foreground">
                Try a different search, or check back later.
              </p>
            </div>
          )}

          {totalPages > 1 ? (
            <div className="flex items-center justify-center gap-2 pt-8">
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="border-border"
                disabled={page === 1 || loading}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
              >
                <ChevronLeft className="w-4 h-4" />
              </Button>
              <span className="text-xs text-muted-foreground">
                Page {page} of {totalPages}
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="border-border"
                disabled={page === totalPages || loading}
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              >
                <ChevronRight className="w-4 h-4" />
              </Button>
            </div>
          ) : null}
        </SitePageContainer>
      </SiteMain>
      <SiteFooter />
    </div>
  )
}
