import { Badge } from '@/components/ui/badge'
import type { AnalyticsProductAvailability } from '@/lib/analytics/analytics-types'

interface AnalyticsAvailabilityBadgeProps {
  availability: AnalyticsProductAvailability
}

export function AnalyticsAvailabilityBadge({ availability }: AnalyticsAvailabilityBadgeProps) {
  switch (availability) {
    case 'ACTIVE':
      return (
        <Badge variant="outline" className="border-emerald-500/40 text-emerald-300">
          Active
        </Badge>
      )
    case 'UNAVAILABLE':
      return (
        <Badge variant="outline" className="border-amber-500/40 text-amber-200">
          Unavailable
        </Badge>
      )
    case 'ARCHIVED':
      return (
        <Badge variant="outline" className="border-muted-foreground/40 text-muted-foreground">
          Archived
        </Badge>
      )
  }
}
