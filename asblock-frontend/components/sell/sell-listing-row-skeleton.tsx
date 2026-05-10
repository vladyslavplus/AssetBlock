"use client";

import { Skeleton } from "@/components/ui/skeleton";

export function SellListingRowSkeleton() {
  return (
    <li
      className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 rounded-lg border border-border bg-card-elevated px-4 py-3"
      aria-hidden
    >
      <div className="min-w-0 space-y-2 flex-1">
        <Skeleton className="h-4 w-[min(100%,18rem)] rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-3 w-32 rounded-sm bg-muted-foreground/20 animate-pulse" />
      </div>
      <div className="flex flex-wrap gap-2 shrink-0">
        <Skeleton className="h-8 w-16 rounded-md bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-8 w-20 rounded-md bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-8 w-20 rounded-md bg-muted-foreground/20 animate-pulse" />
      </div>
    </li>
  );
}

export function SellListingListSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <ul className="space-y-3" aria-busy="true" aria-label="Loading listings">
      {Array.from({ length: rows }, (_, i) => (
        <SellListingRowSkeleton key={i} />
      ))}
    </ul>
  );
}
