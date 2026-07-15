'use client'

import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'

export function AccountProfileCardSkeleton() {
  return (
    <Card className="border-border bg-card-elevated">
      <CardHeader className="space-y-2">
        <Skeleton className="h-6 w-32 rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-3.5 w-[min(100%,20rem)] rounded-sm bg-muted-foreground/20 animate-pulse" />
      </CardHeader>
      <CardContent className="space-y-4" aria-busy="true" aria-label="Loading profile">
        {Array.from({ length: 5 }, (_, i) => (
          <div key={i} className="space-y-1.5">
            <Skeleton className="h-3 w-24 rounded-sm bg-muted-foreground/20 animate-pulse" />
            <Skeleton className="h-9 w-full rounded-md bg-muted-foreground/20 animate-pulse" />
          </div>
        ))}
        <Skeleton className="h-20 w-full rounded-md bg-muted-foreground/15 animate-pulse" />
        <Skeleton className="h-10 w-28 rounded-md bg-muted-foreground/20 animate-pulse" />
      </CardContent>
    </Card>
  )
}

export function SocialLinksFieldsSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <div className="space-y-3" aria-busy="true" aria-label="Loading social platforms">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="space-y-1.5">
          <Skeleton className="h-3 w-28 rounded-sm bg-muted-foreground/20 animate-pulse" />
          <Skeleton className="h-9 w-full rounded-md bg-muted-foreground/20 animate-pulse" />
        </div>
      ))}
    </div>
  )
}
