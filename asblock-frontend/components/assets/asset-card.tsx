'use client'

import Link from 'next/link'
import { appendAnalyticsQuery } from '@/lib/analytics/telemetry-source'
import type { AnalyticsSourceQuery } from '@/lib/analytics/telemetry-constants'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import { routes } from '@/lib/routes'
import { formatUsdWhole } from '@/lib/format-currency'
import { StarRating } from '@/components/assets/star-rating'
import { cn } from '@/lib/utils'

export interface AssetCardProps {
  asset: AssetListItem
  variant?: 'grid' | 'carousel'
  linkSource?: AnalyticsSourceQuery
  collectionId?: string
}

export function AssetCard({
  asset,
  variant = 'grid',
  linkSource = 'catalog',
  collectionId,
}: AssetCardProps) {
  const assetHref = appendAnalyticsQuery(routes.assetDetail(asset.id), linkSource, { collectionId })
  const visibleTags = asset.tags.slice(0, 3)
  const overflowCount = Math.max(0, asset.tags.length - 3)
  const isCarousel = variant === 'carousel'

  return (
    <article
      className={cn(
        'rounded-xl border border-border group transition-smooth hover:border-primary/50 hover:bg-card-elevated hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)] focus-within:ring-2 focus-within:ring-primary focus-within:ring-offset-2 focus-within:ring-offset-background',
        isCarousel
          ? 'flex min-h-[19rem] h-full w-72 flex-none flex-col gap-4 p-5 sm:w-80 min-w-0'
          : 'flex-none w-full p-4 flex flex-col gap-3',
      )}
      style={{ background: '#11101A' }}
    >
      <div className="flex items-start justify-between gap-2 h-12">
        <div className="flex flex-col gap-1.5 min-w-0">
          {asset.categoryName && (
            <span className="text-[10px] font-mono text-muted-foreground uppercase tracking-wider border border-border px-2 py-0.5 rounded w-fit bg-secondary">
              {asset.categoryName}
            </span>
          )}
          <h3 className="line-clamp-2 break-words text-balance text-sm font-semibold leading-snug text-foreground">
            {asset.title}
          </h3>
        </div>
        <span
          className={cn(
            'font-semibold text-foreground shrink-0 font-mono',
            isCarousel ? 'text-lg' : 'text-base',
          )}
        >
          {formatUsdWhole(asset.price)}
        </span>
      </div>

      {isCarousel ? (
        <div className="flex min-h-[2.5rem] min-w-0 flex-1 flex-col">
          {asset.description ? (
            <p className="line-clamp-2 min-w-0 break-words text-xs leading-relaxed text-muted-foreground [overflow-wrap:anywhere]">
              {asset.description}
            </p>
          ) : (
            <span className="text-xs text-muted-foreground/40" aria-hidden="true">
              &nbsp;
            </span>
          )}
        </div>
      ) : (
        <>
          {asset.description && (
            <p className="line-clamp-2 min-w-0 flex-1 break-words text-xs leading-relaxed text-muted-foreground [overflow-wrap:anywhere]">
              {asset.description}
            </p>
          )}
          {!asset.description && <div className="flex-1" />}
        </>
      )}

      {asset.tags.length > 0 && (
        <div className={cn('flex flex-wrap gap-1.5', isCarousel ? 'min-h-7 content-start' : 'h-7')}>
          {visibleTags.map((tag) => (
            <span
              key={tag}
              className="px-2 py-0.5 rounded text-[10px] font-mono bg-secondary text-muted-foreground border border-border"
            >
              {tag}
            </span>
          ))}
          {overflowCount > 0 && (
            <span className="px-2 py-0.5 rounded text-[10px] font-mono bg-secondary text-muted-foreground border border-border">
              +{overflowCount}
            </span>
          )}
        </div>
      )}

      <div
        className={cn('border-t border-border pt-3 flex flex-col gap-3', isCarousel && 'mt-auto')}
      >
        <div className="flex items-center justify-between">
          <Link
            href={routes.userProfile(asset.authorUsername)}
            className="text-xs text-muted-foreground hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card rounded-sm"
          >
            <span className="text-accent">@{asset.authorUsername}</span>
          </Link>
          <StarRating value={asset.averageRating} />
        </div>
        <Link
          href={assetHref}
          className="w-full px-3 py-2 rounded-lg border border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth text-xs font-medium text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card"
        >
          View details
        </Link>
      </div>
    </article>
  )
}
