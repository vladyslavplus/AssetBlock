'use client'

import Link from 'next/link'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'next/navigation'
import { ArrowLeft } from 'lucide-react'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { Badge } from '@/components/ui/badge'
import { useAnalyticsPageView } from '@/hooks/use-analytics-page-view'
import {
  appendAnalyticsQuery,
  resolveTrafficSourceFromLocation,
} from '@/lib/analytics/telemetry-source'
import { trackAnalyticsEvent } from '@/lib/analytics/telemetry-client'
import { ApiRequestError } from '@/lib/http/api-client'
import { collectionKeys, fetchCollectionDetailQuery } from '@/lib/collections/collections-query'
import { formatUsdWhole } from '@/lib/format-currency'
import { routes } from '@/lib/routes'

interface CollectionDetailViewProps {
  collectionId: string
}

export function CollectionDetailView({ collectionId }: CollectionDetailViewProps) {
  const searchParams = useSearchParams()
  const trafficSource = resolveTrafficSourceFromLocation(searchParams)

  useAnalyticsPageView(`collection-view:${collectionId}`, {
    eventType: 'COLLECTION_VIEW',
    collectionId,
    source: trafficSource,
  })

  const detailQuery = useQuery({
    queryKey: collectionKeys.publicDetail(collectionId),
    queryFn: () => fetchCollectionDetailQuery(collectionId),
    retry: (count, err) => {
      if (err instanceof ApiRequestError && err.status === 404) return false
      return count < 2
    },
  })

  if (detailQuery.isPending) {
    return (
      <SiteMain>
        <SitePageContainer variant="wide" padding="none">
          <p className="text-sm text-muted-foreground py-8">Loading collection…</p>
        </SitePageContainer>
      </SiteMain>
    )
  }

  if (detailQuery.isError) {
    const notFound =
      detailQuery.error instanceof ApiRequestError && detailQuery.error.status === 404
    return (
      <SiteMain>
        <SitePageContainer variant="wide" padding="none">
          <Link
            href="/collections"
            className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
          >
            <ArrowLeft className="w-4 h-4" />
            Back to collections
          </Link>
          <p className="text-sm text-destructive" role="alert">
            {notFound
              ? 'This collection is not available.'
              : detailQuery.error instanceof Error
                ? detailQuery.error.message
                : 'Could not load collection.'}
          </p>
        </SitePageContainer>
      </SiteMain>
    )
  }

  const detail = detailQuery.data
  const items = [...detail.items].sort((a, b) => a.position - b.position)

  return (
    <SiteMain>
      <SitePageContainer variant="wide" padding="none">
        <Link
          href="/collections"
          className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
        >
          <ArrowLeft className="w-4 h-4" />
          Back to collections
        </Link>

        <div className="space-y-2 mb-8">
          <Badge variant="secondary" className="text-[10px]">
            Editorial collection
          </Badge>
          <h1 className="text-3xl font-semibold text-balance">{detail.title}</h1>
          <p className="text-sm text-muted-foreground">
            Curated by{' '}
            <Link
              href={`/users/${encodeURIComponent(detail.sellerUsername)}`}
              className="text-accent hover:underline"
            >
              @{detail.sellerUsername}
            </Link>
            {' · '}
            {items.length} {items.length === 1 ? 'asset' : 'assets'}
          </p>
          {detail.description?.trim() ? (
            <p className="text-sm text-foreground leading-relaxed whitespace-pre-wrap pt-2 max-w-3xl">
              {detail.description}
            </p>
          ) : null}
        </div>

        <ul className="space-y-3">
          {items.map((item) => (
            <li
              key={item.assetId}
              className="rounded-lg border border-border bg-card-elevated px-4 py-3 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2"
            >
              <div className="min-w-0">
                <p className="font-medium text-foreground line-clamp-2">{item.title}</p>
                <p className="text-xs text-muted-foreground font-mono mt-0.5">
                  {formatUsdWhole(item.price)}
                  {!item.isAvailable && item.unavailableReason ? (
                    <span className="text-amber-500/90"> · {item.unavailableReason}</span>
                  ) : null}
                </p>
              </div>
              {item.isAvailable ? (
                <Link
                  href={appendAnalyticsQuery(routes.assetDetail(item.assetId), 'collection', {
                    collectionId,
                  })}
                  onClick={() => {
                    trackAnalyticsEvent({
                      eventType: 'COLLECTION_ITEM_CLICK',
                      assetId: item.assetId,
                      collectionId,
                      source: 'COLLECTION',
                    })
                  }}
                  className="text-xs font-medium text-primary hover:text-primary/80 shrink-0"
                >
                  View asset →
                </Link>
              ) : (
                <span className="text-xs text-muted-foreground">Unavailable</span>
              )}
            </li>
          ))}
        </ul>
      </SitePageContainer>
    </SiteMain>
  )
}
