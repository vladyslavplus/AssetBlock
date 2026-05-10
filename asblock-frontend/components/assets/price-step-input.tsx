'use client'

import { Minus, Plus } from 'lucide-react'
import { cn } from '@/lib/utils'

interface PriceStepInputProps {
  id?: string
  'aria-label'?: string
  value: number | null
  onChange: (v: number | null) => void
  placeholder: string
  mode: 'min' | 'max'
  siblingBound: number | null
}

export function PriceStepInput({
  id,
  'aria-label': ariaLabel,
  value,
  onChange,
  placeholder,
  mode,
  siblingBound,
}: PriceStepInputProps) {
  const step = (delta: number) => {
    if (value === null && delta < 0) {
      return
    }
    const base = value ?? 0
    let next = base + delta
    if (next < 0) {
      onChange(null)
      return
    }
    if (mode === 'min' && siblingBound !== null) {
      next = Math.min(next, siblingBound)
    }
    if (mode === 'max' && siblingBound !== null) {
      next = Math.max(next, siblingBound)
    }
    onChange(next)
  }

  const handleInputChange = (raw: string) => {
    if (raw === '') {
      onChange(null)
      return
    }
    const n = Number.parseInt(raw, 10)
    if (Number.isNaN(n) || n < 0) {
      return
    }
    let next = n
    if (mode === 'min' && siblingBound !== null) {
      next = Math.min(next, siblingBound)
    }
    if (mode === 'max' && siblingBound !== null) {
      next = Math.max(next, siblingBound)
    }
    onChange(next)
  }

  const atMinBound = value === null || value <= 0
  const atMaxBound =
    mode === 'min' && siblingBound !== null && value !== null && value >= siblingBound
  const atMinBoundMax =
    mode === 'max' && siblingBound !== null && value !== null && value <= siblingBound

  return (
    <div className="relative flex-1 min-w-0">
      <input
        id={id}
        aria-label={ariaLabel}
        type="number"
        min={0}
        placeholder={placeholder}
        value={value ?? ''}
        onChange={(e) => handleInputChange(e.target.value)}
        className={cn(
          'no-native-number-spinner flex h-8 w-full rounded-md border border-border bg-input px-2 py-1 pr-[4.25rem] text-xs text-foreground',
          'placeholder:text-muted-foreground/50 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
        )}
      />
      <div
        className="absolute right-1 top-1/2 flex -translate-y-1/2 items-center gap-px rounded border border-border/80 bg-secondary/80 p-0.5"
        role="group"
        aria-label={ariaLabel ? `${ariaLabel} step` : 'Price step'}
      >
        <button
          type="button"
          className="flex size-6 items-center justify-center rounded-sm text-foreground hover:bg-muted/80 disabled:pointer-events-none disabled:opacity-35"
          onClick={() => step(-1)}
          disabled={atMinBound || atMinBoundMax}
          aria-label="Decrease by 1"
        >
          <Minus className="size-3 shrink-0" />
        </button>
        <button
          type="button"
          className="flex size-6 items-center justify-center rounded-sm text-foreground hover:bg-muted/80 disabled:pointer-events-none disabled:opacity-35"
          onClick={() => step(1)}
          disabled={atMaxBound}
          aria-label="Increase by 1"
        >
          <Plus className="size-3 shrink-0" />
        </button>
      </div>
    </div>
  )
}
