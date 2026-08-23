'use client'

import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

function NeutralBar({ className }: { className?: string }) {
  return (
    <Skeleton
      className={cn(
        'rounded-sm bg-muted-foreground/20 animate-pulse motion-reduce:animate-none',
        className,
      )}
    />
  )
}

export function SellCollectionRowSkeleton() {
  return (
    <li className="rounded-lg border border-border bg-card-elevated/40 px-4 py-3" aria-hidden>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0 flex-1 space-y-2">
          <NeutralBar className="h-4 w-[min(100%,16rem)]" />
          <NeutralBar className="h-3 w-24" />
        </div>
        <NeutralBar className="h-5 w-16 rounded-full shrink-0" />
      </div>
    </li>
  )
}

export function SellCollectionListSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <ul className="space-y-2" aria-busy="true" aria-label="Loading collections">
      {Array.from({ length: rows }, (_, i) => (
        <SellCollectionRowSkeleton key={i} />
      ))}
    </ul>
  )
}

export function SellBundleRowSkeleton() {
  return (
    <li
      className="rounded-lg border border-border bg-card-elevated px-4 py-3 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3"
      aria-hidden
    >
      <div className="min-w-0 flex-1 space-y-2">
        <NeutralBar className="h-4 w-[min(100%,18rem)]" />
        <NeutralBar className="h-3 w-40" />
      </div>
      <div className="flex flex-wrap gap-2 shrink-0">
        <NeutralBar className="h-8 w-16 rounded-md" />
        <NeutralBar className="h-8 w-20 rounded-md" />
      </div>
    </li>
  )
}

export function SellBundleListSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <ul className="space-y-2" aria-busy="true" aria-label="Loading bundles">
      {Array.from({ length: rows }, (_, i) => (
        <SellBundleRowSkeleton key={i} />
      ))}
    </ul>
  )
}

export function SellFormSkeleton({
  fields = 4,
  label = 'Loading form',
}: {
  fields?: number
  label?: string
}) {
  return (
    <div className="max-w-lg space-y-5" aria-busy="true" aria-label={label}>
      {Array.from({ length: fields }, (_, i) => (
        <div key={i} className="space-y-1.5">
          <NeutralBar className="h-3 w-20" />
          <NeutralBar className={cn('h-9 w-full rounded-md', i === 1 && 'h-36 sm:h-40 md:h-36')} />
        </div>
      ))}
      <NeutralBar className="h-9 w-full sm:w-36 rounded-md" />
    </div>
  )
}

export function SellCollectionManagementSkeleton() {
  return (
    <div
      className="rounded-lg border border-border bg-card-elevated p-4 space-y-4"
      aria-busy="true"
      aria-label="Loading collection"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <NeutralBar className="h-4 w-36" />
        <NeutralBar className="h-5 w-16 rounded-full" />
      </div>
      <SellFormSkeleton fields={2} label="Loading collection metadata" />
      <div className="space-y-2 border-t border-border/50 pt-4">
        <NeutralBar className="h-3 w-16" />
        <NeutralBar className="h-12 w-full rounded-md" />
        <NeutralBar className="h-12 w-full rounded-md" />
        <NeutralBar className="h-9 w-full rounded-md" />
      </div>
    </div>
  )
}

export function SellAssetChecklistSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <ul className="space-y-2" aria-busy="true" aria-label="Loading assets">
      {Array.from({ length: rows }, (_, i) => (
        <li
          key={i}
          className="flex items-start gap-3 rounded-md border border-border/50 px-3 py-2"
          aria-hidden
        >
          <NeutralBar className="mt-0.5 size-4 rounded-sm shrink-0" />
          <div className="min-w-0 flex-1 space-y-2">
            <NeutralBar className="h-4 w-[min(100%,14rem)]" />
            <NeutralBar className="h-3 w-16" />
          </div>
        </li>
      ))}
    </ul>
  )
}

export function SellSelectControlSkeleton({ label = 'Loading options' }: { label?: string }) {
  return (
    <div aria-busy="true" aria-label={label}>
      <NeutralBar className="h-9 w-full rounded-md" />
    </div>
  )
}
