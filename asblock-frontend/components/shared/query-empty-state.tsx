import type { ComponentType, ReactNode } from 'react'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { cn } from '@/lib/utils'

export interface QueryEmptyStateProps {
  icon?: ComponentType<{ className?: string; 'aria-hidden'?: boolean | 'true' | 'false' }>
  title: ReactNode
  description?: ReactNode
  action?: ReactNode
  className?: string
  compact?: boolean
  headingLevel?: 'h2' | 'h3' | 'p'
}

export function QueryEmptyState({
  icon: Icon,
  title,
  description,
  action,
  className,
  compact = false,
  headingLevel = 'p',
}: QueryEmptyStateProps) {
  const TitleTag = headingLevel

  return (
    <Empty className={cn(compact ? 'gap-2 p-4 md:p-6' : 'p-6 md:p-12', className)}>
      <EmptyHeader className={cn(compact && 'gap-1 max-w-xs')}>
        {Icon ? (
          <EmptyMedia className={cn(compact ? 'mb-1' : 'mb-2')}>
            <Icon
              className={cn(
                compact ? 'size-8 text-muted-foreground/50' : 'h-10 w-10 text-muted-foreground/50',
              )}
              aria-hidden
            />
          </EmptyMedia>
        ) : null}
        <EmptyTitle
          className={cn(
            compact
              ? 'text-sm font-medium text-foreground'
              : 'text-base font-semibold text-foreground',
          )}
        >
          <TitleTag>{title}</TitleTag>
        </EmptyTitle>
        {description ? (
          <EmptyDescription
            className={cn(
              compact
                ? 'text-xs text-muted-foreground leading-relaxed max-w-[18rem]'
                : 'text-sm text-muted-foreground',
            )}
          >
            {description}
          </EmptyDescription>
        ) : null}
      </EmptyHeader>
      {action ? <EmptyContent>{action}</EmptyContent> : null}
    </Empty>
  )
}
