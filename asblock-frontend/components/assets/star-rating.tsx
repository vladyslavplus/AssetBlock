import { Star } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface StarRatingProps {
  value: number
  size?: 'sm' | 'md'
  tone?: 'yellow' | 'accent'
  showValue?: boolean
  className?: string
}

export function StarRating({
  value,
  size = 'sm',
  tone = 'yellow',
  showValue = true,
  className,
}: StarRatingProps) {
  const clamped = Math.max(0, Math.min(5, Number.isFinite(value) ? value : 0))
  const rounded = Math.round(clamped * 2) / 2
  const fullStars = Math.floor(rounded)
  const hasHalfStar = rounded % 1 !== 0

  const sizeClasses = {
    sm: 'w-3 h-3',
    md: 'w-4 h-4',
  }[size]

  const activeColor =
    tone === 'accent' ? 'fill-accent text-accent' : 'fill-yellow-400 text-yellow-400'
  const inactiveColor = tone === 'accent' ? 'text-muted-foreground/30' : 'text-muted-foreground/20'

  return (
    <div
      role="img"
      className={cn('flex items-center gap-1.5', className)}
      aria-label={`Rating: ${clamped.toFixed(1)} out of 5`}
    >
      <div className="flex items-center gap-0.5" aria-hidden="true">
        {[1, 2, 3, 4, 5].map((i) => {
          const isFull = i <= fullStars
          const isHalf = !isFull && i === Math.ceil(rounded) && hasHalfStar

          if (isFull) {
            return <Star key={`star-${i}`} className={cn(sizeClasses, activeColor)} />
          }

          if (isHalf) {
            return (
              <div key={`star-${i}`} className="relative inline-flex">
                <Star className={cn(sizeClasses, inactiveColor)} />
                <div className="absolute inset-0 w-1/2 overflow-hidden">
                  <Star className={cn(sizeClasses, activeColor)} />
                </div>
              </div>
            )
          }

          return <Star key={`star-${i}`} className={cn(sizeClasses, inactiveColor)} />
        })}
      </div>
      {showValue ? (
        <span
          className="text-xs text-muted-foreground ml-0.5 font-mono tabular-nums"
          aria-hidden="true"
        >
          {clamped.toFixed(1)}
        </span>
      ) : null}
    </div>
  )
}
