'use client'

import { AlertCircle, RotateCcw } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

interface AnalyticsSectionErrorProps {
  title: string
  message: string
  onRetry?: () => void
  className?: string
}

export function AnalyticsSectionError({
  title,
  message,
  onRetry,
  className,
}: AnalyticsSectionErrorProps) {
  return (
    <div
      role="alert"
      className={cn(
        'rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-4 text-sm',
        className,
      )}
    >
      <div className="flex items-start gap-3">
        <AlertCircle className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
        <div className="min-w-0 flex-1 space-y-2">
          <p className="font-medium text-destructive">{title}</p>
          <p className="text-destructive/90">{message}</p>
          {onRetry ? (
            <Button
              type="button"
              size="sm"
              variant="outline"
              className="border-destructive/40 bg-transparent text-destructive hover:bg-destructive/10"
              onClick={onRetry}
            >
              <RotateCcw className="mr-2 size-3.5" aria-hidden />
              Retry
            </Button>
          ) : null}
        </div>
      </div>
    </div>
  )
}
