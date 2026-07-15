'use client'

import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

export function LibraryPurchaseCardSkeleton() {
  return (
    <div className="bg-card-elevated border border-border rounded-xl p-4 space-y-3" aria-hidden>
      <Skeleton className="h-5 w-[92%] rounded-sm bg-muted-foreground/20 animate-pulse" />
      <Skeleton className="h-3 w-40 rounded-sm bg-muted-foreground/20 animate-pulse" />
      <div className="flex items-center justify-between pt-1">
        <Skeleton className="h-4 w-16 rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-3 w-24 rounded-sm bg-muted-foreground/20 animate-pulse" />
      </div>
      <div className="flex flex-col gap-2 pt-2 border-t border-border/30">
        <div className="flex gap-2">
          <Skeleton className="h-8 flex-1 rounded-md bg-muted-foreground/20 animate-pulse" />
          <Skeleton className="h-8 flex-1 rounded-md bg-muted-foreground/20 animate-pulse" />
        </div>
      </div>
    </div>
  )
}

export function LibraryGridSkeleton({
  count = 6,
  className,
}: {
  count?: number
  className?: string
}) {
  return (
    <div
      className={cn('grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4', className)}
      aria-busy="true"
      aria-label="Loading library"
    >
      {Array.from({ length: count }, (_, i) => (
        <LibraryPurchaseCardSkeleton key={i} />
      ))}
    </div>
  )
}
