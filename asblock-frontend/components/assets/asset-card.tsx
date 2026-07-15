'use client'

import Link from 'next/link'
import { Star } from 'lucide-react'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import { formatUsdWhole } from '@/lib/format-currency'

interface AssetCardProps {
  asset: AssetListItem
}

export function AssetCard({ asset }: AssetCardProps) {
  const visibleTags = asset.tags.slice(0, 3)
  const overflowCount = Math.max(0, asset.tags.length - 3)

  return (
    <article
      className="flex-none w-full rounded-xl border border-border p-4 flex flex-col gap-3 group transition-smooth hover:border-primary/50 hover:bg-card-elevated hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)] focus-within:ring-2 focus-within:ring-primary focus-within:ring-offset-2 focus-within:ring-offset-background"
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
        <span className="text-base font-semibold text-foreground shrink-0 font-mono">
          {formatUsdWhole(asset.price)}
        </span>
      </div>

      {asset.description && (
        <p className="line-clamp-2 min-w-0 flex-1 break-words text-xs leading-relaxed text-muted-foreground [overflow-wrap:anywhere]">
          {asset.description}
        </p>
      )}
      {!asset.description && <div className="flex-1" />}

      {asset.tags.length > 0 && (
        <div className="flex flex-wrap gap-1.5 h-7">
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

      <div className="border-t border-border pt-3 flex flex-col gap-3">
        <div className="flex items-center justify-between">
          <Link
            href={`/users/${encodeURIComponent(asset.authorUsername)}`}
            className="text-xs text-muted-foreground hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card rounded-sm"
          >
            <span className="text-accent">@{asset.authorUsername}</span>
          </Link>
          <div className="flex items-center gap-1">
            <div className="flex gap-0.5">
              {[...Array(5)].map((_, i) => (
                <Star
                  key={i}
                  className={`w-3 h-3 ${
                    i < Math.round(asset.averageRating)
                      ? 'fill-yellow-400 text-yellow-400'
                      : 'text-muted-foreground/20'
                  }`}
                />
              ))}
            </div>
            <span className="text-xs text-muted-foreground ml-1">
              {asset.averageRating.toFixed(1)}
            </span>
          </div>
        </div>
        <Link
          href={`/assets/${asset.id}`}
          className="w-full px-3 py-2 rounded-lg border border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth text-xs font-medium text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card"
        >
          View details
        </Link>
      </div>
    </article>
  )
}
