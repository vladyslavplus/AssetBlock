'use client'

import Link from 'next/link'
import type { BundleListItem } from '@/lib/bundles/bundle-types'
import { formatUsdWhole } from '@/lib/format-currency'

interface BundleCardProps {
  bundle: BundleListItem
}

export function BundleCard({ bundle }: BundleCardProps) {
  const savings =
    bundle.savingsAmount > 0
      ? `Save ${formatUsdWhole(bundle.savingsAmount)} (${Math.round(bundle.savingsPercent)}%)`
      : null

  return (
    <article
      className="flex-none w-full rounded-xl border border-border p-4 flex flex-col gap-3 group transition-smooth hover:border-primary/50 hover:bg-card-elevated hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)] focus-within:ring-2 focus-within:ring-primary focus-within:ring-offset-2 focus-within:ring-offset-background"
      style={{ background: '#11101A' }}
    >
      <div className="flex items-start justify-between gap-2">
        <div className="flex flex-col gap-1.5 min-w-0">
          <span className="text-[10px] font-mono text-muted-foreground uppercase tracking-wider border border-border px-2 py-0.5 rounded w-fit bg-secondary">
            Bundle
          </span>
          <h3 className="line-clamp-2 break-words text-balance text-sm font-semibold leading-snug text-foreground">
            {bundle.title}
          </h3>
        </div>
        <span className="text-base font-semibold text-foreground shrink-0 font-mono">
          {formatUsdWhole(bundle.price)}
        </span>
      </div>

      {bundle.description ? (
        <p className="line-clamp-2 min-w-0 flex-1 break-words text-xs leading-relaxed text-muted-foreground [overflow-wrap:anywhere]">
          {bundle.description}
        </p>
      ) : (
        <div className="flex-1" />
      )}

      <div className="space-y-1 text-xs text-muted-foreground">
        <p>
          List total{' '}
          <span className="font-mono text-foreground/80 line-through">
            {formatUsdWhole(bundle.listPriceTotal)}
          </span>
        </p>
        {savings ? <p className="text-accent font-medium">{savings}</p> : null}
      </div>

      <div className="border-t border-border pt-3 flex items-center justify-between gap-2">
        <Link
          href={`/users/${encodeURIComponent(bundle.sellerUsername)}`}
          className="text-xs text-muted-foreground hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 rounded-sm"
        >
          <span className="text-accent">@{bundle.sellerUsername}</span>
        </Link>
        <span className="text-xs text-muted-foreground font-mono tabular-nums">
          {bundle.itemCount} assets
        </span>
      </div>

      <Link
        href={`/bundles/${bundle.id}`}
        className="text-xs font-medium text-primary hover:text-primary/80 transition-colors"
      >
        View bundle →
      </Link>
    </article>
  )
}
