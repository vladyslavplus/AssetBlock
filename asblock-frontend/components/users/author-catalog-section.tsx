'use client'

import { useQuery } from '@tanstack/react-query'
import Link from 'next/link'
import { AssetCard } from '@/components/assets/asset-card'
import { Button } from '@/components/ui/button'
import type { AuthorCatalogPageResult } from '@/lib/server/user-profile-server'
import { fetchAuthorCatalogClient, userProfileKeys } from '@/lib/profile/user-profile-query'

function ProfilePagination({
  username,
  page,
  totalPages,
}: {
  username: string
  page: number
  totalPages: number
}) {
  if (totalPages <= 1) {
    return null
  }
  const basePath = `/users/${encodeURIComponent(username)}`
  const prevHref = page <= 2 ? basePath : `${basePath}?page=${page - 1}`
  const nextHref = `${basePath}?page=${page + 1}`

  return (
    <div className="flex flex-wrap items-center justify-center gap-3 pt-10">
      {page > 1 ? (
        <Button asChild variant="outline" size="sm" className="min-w-[7rem]">
          <Link href={prevHref}>Previous</Link>
        </Button>
      ) : (
        <Button variant="outline" size="sm" className="min-w-[7rem]" disabled>
          Previous
        </Button>
      )}
      <span className="text-sm text-muted-foreground tabular-nums px-2">
        Page {page} of {totalPages}
      </span>
      {page < totalPages ? (
        <Button asChild variant="outline" size="sm" className="min-w-[7rem]">
          <Link href={nextHref}>Next</Link>
        </Button>
      ) : (
        <Button variant="outline" size="sm" className="min-w-[7rem]" disabled>
          Next
        </Button>
      )}
    </div>
  )
}

interface AuthorCatalogSectionProps {
  authorId: string
  username: string
  initialCatalog: AuthorCatalogPageResult
}

export function AuthorCatalogSection({
  authorId,
  username,
  initialCatalog,
}: AuthorCatalogSectionProps) {
  const page = initialCatalog.page

  const catalogQuery = useQuery({
    queryKey: userProfileKeys.authorCatalog(authorId, page),
    queryFn: () => fetchAuthorCatalogClient(authorId, page),
    initialData: initialCatalog,
    staleTime: 2 * 60 * 1000,
  })

  const catalog = catalogQuery.data

  return (
    <section aria-labelledby="author-assets-heading">
      <div className="flex flex-col gap-1 sm:flex-row sm:items-end sm:justify-between mb-6">
        <h2 id="author-assets-heading" className="text-xl font-semibold text-foreground">
          Listings
        </h2>
        <p className="text-sm text-muted-foreground tabular-nums">
          {catalog.totalCount === 0
            ? 'No published assets'
            : `${catalog.totalCount} ${catalog.totalCount === 1 ? 'asset' : 'assets'}`}
        </p>
      </div>

      {catalog.items.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border/80 bg-card-elevated/50 px-6 py-14 text-center">
          <p className="text-sm text-muted-foreground max-w-md mx-auto">
            @{username} does not have any listings in the catalog yet.
          </p>
          <Button asChild variant="outline" className="mt-6 border-border">
            <Link href="/assets">Browse all assets</Link>
          </Button>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            {catalog.items.map((asset) => (
              <AssetCard key={asset.id} asset={asset} />
            ))}
          </div>
          <ProfilePagination
            username={username}
            page={catalog.page}
            totalPages={catalog.totalPages}
          />
        </>
      )}
    </section>
  )
}
