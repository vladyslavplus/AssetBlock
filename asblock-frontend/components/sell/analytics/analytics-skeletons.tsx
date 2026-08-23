import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

function NeutralSkeleton({ className }: { className?: string }) {
  return (
    <Skeleton
      className={cn('bg-muted-foreground/20 animate-pulse motion-reduce:animate-none', className)}
    />
  )
}

export function AnalyticsKpiSkeletonGrid() {
  return (
    <div
      className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3"
      aria-busy="true"
      aria-label="Loading analytics KPIs"
    >
      {Array.from({ length: 6 }).map((_, index) => (
        <div
          key={index}
          className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[7.5rem]"
        >
          <NeutralSkeleton className="mb-3 h-4 w-28" />
          <NeutralSkeleton className="mb-2 h-8 w-36" />
          <NeutralSkeleton className="h-3 w-20" />
        </div>
      ))}
    </div>
  )
}

export function AnalyticsChartSkeleton() {
  return (
    <div
      className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[20rem]"
      aria-busy="true"
      aria-label="Loading analytics chart"
    >
      <NeutralSkeleton className="mb-4 h-5 w-40" />
      <NeutralSkeleton className="h-[16rem] w-full" />
    </div>
  )
}

export function AnalyticsTableSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div
      className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[18rem]"
      aria-busy="true"
      aria-label="Loading analytics table"
    >
      <NeutralSkeleton className="mb-4 h-5 w-48" />
      <div className="space-y-3">
        {Array.from({ length: rows }).map((_, index) => (
          <NeutralSkeleton key={index} className="h-10 w-full" />
        ))}
      </div>
    </div>
  )
}

export function AnalyticsListSkeleton({ rows = 4 }: { rows?: number }) {
  return (
    <div
      className="rounded-lg border border-border/60 bg-card/40 p-4 min-h-[14rem]"
      aria-busy="true"
      aria-label="Loading analytics list"
    >
      <NeutralSkeleton className="mb-4 h-5 w-36" />
      <div className="space-y-3">
        {Array.from({ length: rows }).map((_, index) => (
          <NeutralSkeleton key={index} className="h-12 w-full" />
        ))}
      </div>
    </div>
  )
}

/** Full dashboard shape for dynamic-import / initial analytics load. */
export function AnalyticsDashboardSkeleton() {
  return (
    <div className="space-y-8" aria-busy="true" aria-label="Loading analytics dashboard">
      <div className="space-y-2">
        <NeutralSkeleton className="h-4 w-full max-w-xl" />
        <NeutralSkeleton className="h-3 w-48" />
      </div>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <NeutralSkeleton className="h-9 w-full max-w-sm rounded-md" />
        <NeutralSkeleton className="h-9 w-28 rounded-md" />
      </div>
      <AnalyticsKpiSkeletonGrid />
      <AnalyticsChartSkeleton />
      <AnalyticsTableSkeleton />
    </div>
  )
}
