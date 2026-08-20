'use client'

import { useState } from 'react'
import { ChevronDown } from 'lucide-react'
import type { AssetLicenseSummaryApi } from '@/lib/assets/license-types'
import { Badge } from '@/components/ui/badge'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { cn } from '@/lib/utils'

interface AssetLicenseCardProps {
  license: AssetLicenseSummaryApi
  className?: string
}

export function AssetLicenseCard({ license, className }: AssetLicenseCardProps) {
  const [open, setOpen] = useState(false)

  return (
    <div
      className={cn('rounded-lg border border-border bg-card-elevated/40 p-4 space-y-3', className)}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold text-foreground">License</h3>
        <Badge variant="secondary" className="text-[10px] font-mono uppercase tracking-wide">
          {license.displayName}
        </Badge>
        <span className="text-[10px] text-muted-foreground font-mono">
          template {license.templateVersion}
        </span>
      </div>

      <Collapsible open={open} onOpenChange={setOpen}>
        <CollapsibleTrigger className="flex w-full items-center justify-between gap-2 rounded-md border border-border/60 bg-secondary/30 px-3 py-2 text-xs text-foreground hover:bg-secondary/50 transition-colors">
          <span>{open ? 'Hide license terms' : 'View license terms'}</span>
          <ChevronDown
            className={cn(
              'size-4 shrink-0 text-muted-foreground transition-transform',
              open && 'rotate-180',
            )}
            aria-hidden
          />
        </CollapsibleTrigger>
        <CollapsibleContent className="pt-3">
          <pre className="asset-license-terms whitespace-pre-wrap break-words text-xs leading-relaxed text-muted-foreground font-sans">
            {license.terms}
          </pre>
        </CollapsibleContent>
      </Collapsible>
    </div>
  )
}
