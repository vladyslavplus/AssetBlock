'use client'

import { useQuery } from '@tanstack/react-query'
import Link from 'next/link'
import { useState } from 'react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { Button } from '@/components/ui/button'
import { QueryEmptyState } from '@/components/shared/query-empty-state'
import { LibraryGridSkeleton } from '@/components/library/library-purchase-card-skeleton'
import { LibraryPurchaseCard } from '@/components/library/library-purchase-card'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import { useAuth } from '@/components/auth/auth-context'
import {
  fetchLibraryPurchasesOrThrow,
  libraryKeys,
  LibraryFetchError,
} from '@/lib/library/library-query'
import { runQueryInBackground } from '@/lib/query/query-refresh'
import { routes } from '@/lib/routes'

const LIBRARY_PAGE_SIZE = 12

export function LibraryPageClient() {
  const { status } = useAuth()
  const authed = status === 'authenticated'
  const [page, setPage] = useState(1)

  const purchasesQuery = useQuery({
    queryKey: libraryKeys.purchases({ page, pageSize: LIBRARY_PAGE_SIZE }),
    queryFn: () => fetchLibraryPurchasesOrThrow({ page, pageSize: LIBRARY_PAGE_SIZE }),
    enabled: authed,
  })

  const purchases = purchasesQuery.data?.items ?? []
  const totalCount = purchasesQuery.data?.totalCount ?? 0
  const totalPages = Math.max(1, Math.ceil(totalCount / LIBRARY_PAGE_SIZE))
  const loading = authed && purchasesQuery.isPending
  const loadError =
    purchasesQuery.error instanceof LibraryFetchError
      ? { status: purchasesQuery.error.status, message: purchasesQuery.error.message }
      : purchasesQuery.isError
        ? { status: 0, message: purchasesQuery.error?.message ?? 'Could not load library.' }
        : null

  const handlePageChange = (newPage: number) => {
    setPage(newPage)
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <SiteMain>
        <SitePageContainer variant="wide">
          <div className="mb-8">
            <h1 className="text-3xl font-bold text-foreground mb-2">My library</h1>
            <p className="text-sm text-muted-foreground">
              {totalCount > 0
                ? `${totalCount} purchased digital asset${totalCount === 1 ? '' : 's'}`
                : 'Your purchased digital assets'}
            </p>
          </div>

          {!authed && status !== 'loading' && (
            <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
              <p className="text-sm text-muted-foreground">Sign in to view your library.</p>
              <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
                <Link href={routes.login(routes.library())}>Sign in</Link>
              </Button>
            </div>
          )}

          {status === 'loading' && <SessionBlockSkeleton />}

          {authed && loading && <LibraryGridSkeleton />}

          {authed && loadError && (
            <div
              className="mb-6 rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
              role="alert"
            >
              <p className="font-medium">Could not load your library</p>
              <p className="mt-1 text-destructive/90">{loadError.message}</p>
              {loadError.status === 401 && (
                <Button
                  asChild
                  variant="outline"
                  size="sm"
                  className="mt-3 border-destructive/50 text-destructive"
                >
                  <Link href={routes.login(routes.library())}>Sign in again</Link>
                </Button>
              )}
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="mt-3"
                onClick={() =>
                  runQueryInBackground(purchasesQuery.refetch({ cancelRefetch: false }))
                }
              >
                Retry
              </Button>
            </div>
          )}

          {authed && purchasesQuery.isSuccess && purchases.length > 0 && (
            <>
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                {purchases.map((purchase) => (
                  <LibraryPurchaseCard key={purchase.id} purchase={purchase} />
                ))}
              </div>

              {totalPages > 1 && (
                <div className="flex flex-wrap items-center justify-center gap-3 pt-10 pb-4">
                  <Button
                    variant="outline"
                    size="sm"
                    className="min-w-[7rem]"
                    disabled={page <= 1 || loading}
                    onClick={() => handlePageChange(page - 1)}
                  >
                    Previous
                  </Button>
                  <span className="text-sm text-muted-foreground tabular-nums px-2">
                    Page {page} of {totalPages}
                  </span>
                  <Button
                    variant="outline"
                    size="sm"
                    className="min-w-[7rem]"
                    disabled={page >= totalPages || loading}
                    onClick={() => handlePageChange(page + 1)}
                  >
                    Next
                  </Button>
                </div>
              )}
            </>
          )}

          {authed && purchasesQuery.isSuccess && purchases.length === 0 && !loading && (
            <QueryEmptyState
              title="No purchases yet"
              description="When you buy an asset, it will appear here. Browse the catalog to get started."
              headingLevel="h2"
              className="border-0 bg-transparent py-16"
              action={
                <Button
                  asChild
                  className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium"
                >
                  <Link href="/assets">Browse assets</Link>
                </Button>
              }
            />
          )}
        </SitePageContainer>
      </SiteMain>

      <SiteFooter />
    </div>
  )
}
