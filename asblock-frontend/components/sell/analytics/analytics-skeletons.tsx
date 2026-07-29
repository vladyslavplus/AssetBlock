import { Skeleton } from '@/components/ui/skeleton'

export function AnalyticsKpiSkeletonGrid() {
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
      {Array.from({ length: 6 }).map((_, index) => (
        <div
          key={index}
          className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[7.5rem]"
        >
          <Skeleton className="mb-3 h-4 w-28" />
          <Skeleton className="mb-2 h-8 w-36" />
          <Skeleton className="h-3 w-20" />
        </div>
      ))}
    </div>
  )
}

export function AnalyticsChartSkeleton() {
  return (
    <div className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[20rem]">
      <Skeleton className="mb-4 h-5 w-40" />
      <Skeleton className="h-[16rem] w-full" />
    </div>
  )
}

export function AnalyticsTableSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[18rem]">
      <Skeleton className="mb-4 h-5 w-48" />
      <div className="space-y-3">
        {Array.from({ length: rows }).map((_, index) => (
          <Skeleton key={index} className="h-10 w-full" />
        ))}
      </div>
    </div>
  )
}

export function AnalyticsListSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <div className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[14rem]">
      <Skeleton className="mb-4 h-5 w-36" />
      <div className="space-y-3">
        {Array.from({ length: rows }).map((_, index) => (
          <Skeleton key={index} className="h-12 w-full" />
        ))}
      </div>
    </div>
  )
}
