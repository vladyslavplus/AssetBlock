'use client'

import { useState } from 'react'
import { Copy } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

interface ContentHashDisplayProps {
  hash: string
  className?: string
}

async function copyHash(value: string) {
  try {
    await navigator.clipboard.writeText(value)
    toast.success('Content hash copied')
  } catch {
    toast.error('Could not copy hash')
  }
}

export function ContentHashDisplay({ hash, className }: ContentHashDisplayProps) {
  const [expanded, setExpanded] = useState(false)
  const trimmed = hash.trim()
  if (!trimmed) return null

  const short = trimmed.length > 16 ? `${trimmed.slice(0, 8)}…${trimmed.slice(-8)}` : trimmed

  return (
    <div
      className={cn('rounded-lg border border-border bg-card-elevated/40 p-3 space-y-2', className)}
    >
      <div className="flex items-center justify-between gap-2">
        <p className="text-xs font-medium text-foreground">Content SHA-256</p>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="h-7 px-2 text-[10px] text-muted-foreground"
          onClick={() => void copyHash(trimmed)}
        >
          <Copy className="size-3 mr-1" aria-hidden />
          Copy
        </Button>
      </div>
      <button
        type="button"
        className="w-full text-left font-mono text-[11px] text-muted-foreground break-all hover:text-foreground transition-colors"
        onClick={() => setExpanded((v) => !v)}
        title={expanded ? 'Click to collapse' : 'Click to expand'}
      >
        {expanded ? trimmed : short}
      </button>
      <p className="text-[10px] text-muted-foreground leading-relaxed">
        SHA-256 of the decrypted package bytes at publish time. Compare after download to verify you
        received the expected file.
      </p>
    </div>
  )
}
