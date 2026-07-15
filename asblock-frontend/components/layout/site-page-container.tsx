import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { siteShellClass, type SiteContentWidth } from '@/lib/site-layout'

const paddingClass = {
  default: 'py-8',
  document: 'py-12',
  none: '',
} as const

export type SitePageContainerPadding = keyof typeof paddingClass

export interface SitePageContainerProps {
  variant: SiteContentWidth
  padding?: SitePageContainerPadding
  className?: string
  children: ReactNode
}

/**
 * Primary page content column: shared max-width + gutters + optional vertical padding.
 */
export function SitePageContainer({
  variant,
  padding = 'default',
  className,
  children,
}: SitePageContainerProps) {
  return (
    <div className={cn(siteShellClass(variant), paddingClass[padding], className)}>{children}</div>
  )
}
