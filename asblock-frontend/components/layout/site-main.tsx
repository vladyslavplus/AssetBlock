import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface SiteMainProps {
  children: ReactNode
  className?: string
}

/** Fixed header offset + consistent bottom spacing for full-bleed app pages. */
export function SiteMain({ children, className }: SiteMainProps) {
  return <main className={cn('flex-1 pt-20 pb-16', className)}>{children}</main>
}
