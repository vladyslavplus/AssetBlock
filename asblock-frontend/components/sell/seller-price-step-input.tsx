'use client'

import { useCallback } from 'react'
import { Minus, Plus } from 'lucide-react'
import { cn } from '@/lib/utils'
import { usePointerStepRepeat } from '@/hooks/use-pointer-step-repeat'

const MIN_USD = 0.01

export interface SellerPriceStepInputProps {
  id?: string
  'aria-label'?: string
  value: number | undefined
  onChange: (value: number | undefined) => void
  onBlur: () => void
}

/**
 * Same UX as catalog min/max price: custom +/- steppers, hold-to-repeat, hidden native spinners.
 * Values are USD with cents; buttons step by $1 (typing still allows decimals).
 */
export function SellerPriceStepInput({
  id,
  'aria-label': ariaLabel,
  value,
  onChange,
  onBlur,
}: SellerPriceStepInputProps) {
  const step = useCallback(
    (delta: number) => {
      if (value === undefined && delta > 0) {
        onChange(1)
        return
      }
      if (value === undefined && delta < 0) {
        return
      }
      const base = value ?? 0
      const next = Math.round((base + delta) * 100) / 100
      if (next < MIN_USD) {
        onChange(undefined)
        return
      }
      onChange(next)
    },
    [value, onChange],
  )

  const repeatMinus = usePointerStepRepeat(useCallback(() => step(-1), [step]))
  const repeatPlus = usePointerStepRepeat(useCallback(() => step(1), [step]))

  const handleInputChange = (raw: string) => {
    if (raw === '') {
      onChange(undefined)
      return
    }
    const n = Number.parseFloat(raw)
    if (Number.isNaN(n) || n < 0) {
      return
    }
    if (n === 0) {
      onChange(undefined)
      return
    }
    const clamped = Math.max(MIN_USD, Math.round(n * 100) / 100)
    onChange(clamped)
  }

  const atFloor = value === undefined || value <= MIN_USD

  return (
    <div className="relative min-w-0 flex-1">
      <input
        id={id}
        aria-label={ariaLabel}
        type="number"
        inputMode="decimal"
        min={MIN_USD}
        step={0.01}
        placeholder="9.99"
        value={value ?? ''}
        onChange={(e) => handleInputChange(e.target.value)}
        onBlur={onBlur}
        className={cn(
          'no-native-number-spinner flex h-9 w-full rounded-md border border-border bg-input px-3 py-1 pr-[4.25rem] font-mono text-sm tabular-nums text-foreground',
          'placeholder:text-muted-foreground/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        )}
      />
      <div
        className="absolute right-1 top-1/2 flex -translate-y-1/2 items-center gap-px rounded border border-border/80 bg-secondary/80 p-0.5"
        role="group"
        aria-label={ariaLabel ? `${ariaLabel} step` : 'Price step'}
      >
        <button
          type="button"
          className="flex size-7 items-center justify-center rounded-sm text-foreground hover:bg-muted/80 disabled:pointer-events-none disabled:opacity-35"
          onPointerDown={(e) => repeatMinus.onPointerDown(atFloor, e)}
          onPointerUp={repeatMinus.onPointerEnd}
          onPointerCancel={repeatMinus.onPointerEnd}
          onLostPointerCapture={repeatMinus.onPointerEnd}
          onKeyDown={(e) => {
            if (e.key !== 'Enter' && e.key !== ' ') return
            e.preventDefault()
            if (atFloor) return
            step(-1)
          }}
          disabled={atFloor}
          aria-label="Decrease price by 1 USD"
        >
          <Minus className="size-3.5 shrink-0" />
        </button>
        <button
          type="button"
          className="flex size-7 items-center justify-center rounded-sm text-foreground hover:bg-muted/80 disabled:pointer-events-none disabled:opacity-35"
          onPointerDown={(e) => repeatPlus.onPointerDown(false, e)}
          onPointerUp={repeatPlus.onPointerEnd}
          onPointerCancel={repeatPlus.onPointerEnd}
          onLostPointerCapture={repeatPlus.onPointerEnd}
          onKeyDown={(e) => {
            if (e.key !== 'Enter' && e.key !== ' ') return
            e.preventDefault()
            step(1)
          }}
          aria-label="Increase price by 1 USD"
        >
          <Plus className="size-3.5 shrink-0" />
        </button>
      </div>
    </div>
  )
}
