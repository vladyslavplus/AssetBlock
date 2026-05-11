"use client";

import { Skeleton } from "@/components/ui/skeleton";

export function AuthFormSkeleton() {
  return (
    <div className="space-y-4 py-2" aria-busy="true" aria-label="Loading form">
      <div className="space-y-1.5">
        <Skeleton className="h-3 w-16 rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-9 w-full rounded-md bg-muted-foreground/20 animate-pulse" />
      </div>
      <div className="space-y-1.5">
        <Skeleton className="h-3 w-20 rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-9 w-full rounded-md bg-muted-foreground/20 animate-pulse" />
      </div>
      <Skeleton className="h-9 w-full rounded-md bg-primary/25 animate-pulse" />
    </div>
  );
}
