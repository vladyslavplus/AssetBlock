'use client'

import { useQuery } from '@tanstack/react-query'
import Link from 'next/link'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { Button } from '@/components/ui/button'
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

export function LibraryPageClient() {
  const { status } = useAuth()
  const authed = status === 'authenticated'

  const purchasesQuery = useQuery({
    queryKey: libraryKeys.purchases(),
    queryFn: fetchLibraryPurchasesOrThrow,
    enabled: authed,
  })

  const purchases = purchasesQuery.data?.items ?? []
  const loading = authed && purchasesQuery.isPending
  const loadError =
    purchasesQuery.error instanceof LibraryFetchError
      ? { status: purchasesQuery.error.status, message: purchasesQuery.error.message }
      : purchasesQuery.isError
        ? { status: 0, message: purchasesQuery.error?.message ?? 'Could not load library.' }
        : null

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <SiteMain>
        <SitePageContainer variant="wide">
          <div className="mb-8">
            <h1 className="text-3xl font-bold text-foreground mb-2">My library</h1>
            <p className="text-sm text-muted-foreground">Your purchased digital assets</p>
          </div>

          {!authed && status !== 'loading' && (
            <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
              <p className="text-sm text-muted-foreground">Sign in to view your library.</p>
              <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
                <Link href="/login?returnUrl=/library">Sign in</Link>
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
                  <Link href="/login?returnUrl=/library">Sign in again</Link>
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
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
              {purchases.map((purchase) => (
                <LibraryPurchaseCard key={purchase.id} purchase={purchase} />
              ))}
            </div>
          )}

          {authed && purchasesQuery.isSuccess && purchases.length === 0 && !loading && (
            <div className="flex flex-col items-center justify-center py-16">
              <h2 className="text-lg font-semibold text-foreground mb-2">No purchases yet</h2>
              <p className="text-sm text-muted-foreground mb-6 text-center max-w-md">
                When you buy an asset, it will appear here. Browse the catalog to get started.
              </p>
              <Button
                asChild
                className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium"
              >
                <Link href="/assets">Browse assets</Link>
              </Button>
            </div>
          )}
        </SitePageContainer>
      </SiteMain>

      <SiteFooter />
    </div>
  )
}
