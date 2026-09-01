import Link from 'next/link'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import { formatUsdWhole } from '@/lib/format-currency'
import { formatLongDate } from '@/lib/format-date'
import { StarRating } from '@/components/assets/star-rating'

interface AssetDetailHeroProps {
  asset: AssetListItem
}

export function AssetDetailHero({ asset }: AssetDetailHeroProps) {
  const hasRating = asset.averageRating > 0
  const rating = hasRating ? Math.round(asset.averageRating * 2) / 2 : 0

  return (
    <div className="flex min-w-0 flex-col gap-4">
      <div className="flex min-w-0 flex-col gap-3">
        <h1 className="break-words text-balance text-3xl font-semibold text-foreground">
          {asset.title}
        </h1>
        <div className="flex items-baseline gap-4">
          <span className="text-4xl font-semibold font-mono text-foreground">
            {formatUsdWhole(asset.price)}
          </span>
          {asset.categoryName && (
            <span className="text-[10px] font-mono text-muted-foreground uppercase tracking-wider border border-border px-2 py-1 rounded bg-secondary">
              {asset.categoryName}
            </span>
          )}
        </div>
      </div>

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between pb-4 border-b border-border">
        <div className="flex items-center gap-3">
          {hasRating ? (
            <>
              <StarRating value={asset.averageRating} size="md" tone="accent" showValue={false} />
              <span className="text-sm text-muted-foreground" aria-hidden="true">
                {rating.toFixed(1)} out of 5
              </span>
            </>
          ) : (
            <span className="text-sm text-muted-foreground">No ratings yet</span>
          )}
        </div>

        <div className="text-sm text-muted-foreground">
          <Link
            href={`/users/${encodeURIComponent(asset.authorUsername)}`}
            className="text-accent hover:underline underline-offset-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-background rounded-sm"
          >
            @{asset.authorUsername}
          </Link>
          {' • '}
          Listed {formatLongDate(asset.createdAt)}
        </div>
      </div>

      {asset.tags.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {asset.tags.map((tag) => (
            <span
              key={tag}
              className="text-[10px] font-mono uppercase tracking-wider border border-border px-2 py-1 rounded bg-secondary/50 text-muted-foreground"
            >
              {tag}
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
