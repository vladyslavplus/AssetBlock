'use client'

import Link from 'next/link'
import { appendAnalyticsQuery } from '@/lib/analytics/telemetry-source'
import type { AnalyticsSourceQuery } from '@/lib/analytics/telemetry-constants'
import type { CollectionListItem } from '@/lib/collections/collection-types'
import { routes } from '@/lib/routes'

interface CollectionCardProps {
  collection: CollectionListItem
  linkSource?: AnalyticsSourceQuery
}

export function CollectionCard({ collection, linkSource = 'catalog' }: CollectionCardProps) {
  const collectionHref = appendAnalyticsQuery(routes.collectionDetail(collection.id), linkSource)
  return (
    <article
      className="flex-none w-full rounded-xl border border-border p-4 flex flex-col gap-3 group transition-smooth hover:border-primary/50 hover:bg-card-elevated hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)] focus-within:ring-2 focus-within:ring-primary focus-within:ring-offset-2 focus-within:ring-offset-background"
      style={{ background: '#11101A' }}
    >
      <div className="flex flex-col gap-1.5 min-w-0">
        <span className="text-[10px] font-mono text-muted-foreground uppercase tracking-wider border border-border px-2 py-0.5 rounded w-fit bg-secondary">
          Collection
        </span>
        <h3 className="line-clamp-2 break-words text-balance text-sm font-semibold leading-snug text-foreground">
          {collection.title}
        </h3>
      </div>

      {collection.description ? (
        <p className="line-clamp-2 min-w-0 flex-1 break-words text-xs leading-relaxed text-muted-foreground [overflow-wrap:anywhere]">
          {collection.description}
        </p>
      ) : (
        <div className="flex-1" />
      )}

      <div className="border-t border-border pt-3 flex items-center justify-between gap-2">
        <Link
          href={routes.userProfile(collection.sellerUsername)}
          className="text-xs text-muted-foreground hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 rounded-sm"
        >
          <span className="text-accent">@{collection.sellerUsername}</span>
        </Link>
        <span className="text-xs text-muted-foreground font-mono tabular-nums">
          {collection.itemCount} {collection.itemCount === 1 ? 'asset' : 'assets'}
        </span>
      </div>

      <Link
        href={collectionHref}
        className="text-xs font-medium text-primary hover:text-primary/80 transition-colors"
      >
        View collection →
      </Link>
    </article>
  )
}
