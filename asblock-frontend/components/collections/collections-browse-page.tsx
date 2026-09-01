'use client'

import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import type { Route } from 'next'
import { AlertCircle, ChevronLeft, ChevronRight } from 'lucide-react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { CollectionCard } from '@/components/collections/collection-card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { collectionKeys, fetchCollectionsListQuery } from '@/lib/collections/collections-query'
import {
  parseCollectionsUrlParams,
  serializeCollectionsUrlParams,
} from '@/lib/collections/collections-url-state'

export function CollectionsBrowsePage() {
  const searchParams = useSearchParams()
  const router = useRouter()
  const pathname = usePathname()

  const filters = parseCollectionsUrlParams(searchParams)
  const [search, setSearch] = useState(filters.search)

  useEffect(() => {
    queueMicrotask(() => setSearch(filters.search))
  }, [filters.search])

  const listQuery = useQuery({
    queryKey: collectionKeys.publicList(filters),
    queryFn: () => fetchCollectionsListQuery(filters),
    placeholderData: keepPreviousData,
  })

  const navigateFilters = (next: { search?: string; page?: number }) => {
    const qs = serializeCollectionsUrlParams(next).toString()
    const target = (qs ? `${pathname}?${qs}` : pathname) as Route
    router.push(target)
  }

  const items = listQuery.data?.items ?? []
  const totalPages = listQuery.data?.totalPages ?? 0
  const loading = listQuery.isPending
  const collectionLinkSource = filters.search.trim() ? 'search' : 'catalog'
  const error = listQuery.isError
    ? listQuery.error instanceof Error
      ? listQuery.error.message
      : 'Could not load collections.'
    : null

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="site">
          <div className="mb-8">
            <h1 className="text-3xl font-semibold text-balance">Collections</h1>
            <p className="mt-2 text-muted-foreground text-sm">
              Editorial groupings curated by sellers. Browse free — no checkout.
            </p>
          </div>

          <form
            className="mb-6 flex flex-col sm:flex-row gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              navigateFilters({ search: search.trim(), page: 1 })
            }}
          >
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search collections…"
              className="bg-input border-border"
              aria-label="Search collections"
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
            <p className="text-sm text-muted-foreground py-8">Loading collections…</p>
          ) : items.length > 0 ? (
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
              {items.map((c) => (
                <CollectionCard key={c.id} collection={c} linkSource={collectionLinkSource} />
              ))}
            </div>
          ) : (
            <div className="rounded-lg border border-border/50 p-12 text-center">
              <AlertCircle className="w-12 h-12 text-muted-foreground/30 mx-auto mb-4" />
              <h3 className="font-semibold text-foreground mb-2">No collections found</h3>
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
                disabled={filters.page === 1 || loading}
                onClick={() =>
                  navigateFilters({
                    search: filters.search,
                    page: Math.max(1, filters.page - 1),
                  })
                }
              >
                <ChevronLeft className="w-4 h-4" />
              </Button>
              <span className="text-xs text-muted-foreground">
                Page {filters.page} of {totalPages}
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="border-border"
                disabled={filters.page === totalPages || loading}
                onClick={() =>
                  navigateFilters({
                    search: filters.search,
                    page: Math.min(totalPages, filters.page + 1),
                  })
                }
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
