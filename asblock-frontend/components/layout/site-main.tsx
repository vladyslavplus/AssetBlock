import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface SiteMainProps {
  children: ReactNode
  className?: string
}

/** Fixed header offset + consistent bottom spacing for full-bleed app pages. */
export function SiteMain({ children, className }: SiteMainProps) {
  return (
    <main
      id="main-content"
      tabIndex={-1}
      className={cn('flex-1 pt-28 pb-16 outline-none', className)}
    >
      {children}
    </main>
  )
}
