'use client'

import { Minus, Plus } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import type { CatalogFilterState } from '@/lib/catalog/use-catalog-filter-state'

interface CatalogPriceFilterProps {
  state: CatalogFilterState
}

export function CatalogPriceFilter({ state }: CatalogPriceFilterProps) {
  const {
    draftMin,
    draftMax,
    applyDraftPrices,
    adjustPrice,
    minMinusHold,
    minPlusHold,
    maxMinusHold,
    maxPlusHold,
  } = state

  return (
    <div className="flex flex-col gap-2">
      <span className="text-xs font-medium">Price range</span>
      <div className="flex gap-2">
        <PriceInput
          id="price-min"
          label="Min"
          value={draftMin}
          placeholder="0"
          onValueChange={(value) => applyDraftPrices(value, draftMax)}
          onDecrease={() => adjustPrice('min', -1)}
          onIncrease={() => adjustPrice('min', 1)}
          decreaseHold={minMinusHold}
          increaseHold={minPlusHold}
        />
        <PriceInput
          id="price-max"
          label="Max"
          value={draftMax}
          placeholder=""
          onValueChange={(value) => applyDraftPrices(draftMin, value)}
          onDecrease={() => adjustPrice('max', -1)}
          onIncrease={() => adjustPrice('max', 1)}
          decreaseHold={maxMinusHold}
          increaseHold={maxPlusHold}
        />
      </div>
    </div>
  )
}

interface HoldHandlers {
  onPointerDown: (disabled: boolean, event: React.PointerEvent<HTMLButtonElement>) => void
  onPointerEnd: (event: React.PointerEvent<HTMLButtonElement>) => void
}

interface PriceInputProps {
  id: string
  label: string
  value: number | null
  placeholder: string
  onValueChange: (value: number | null) => void
  onDecrease: () => void
  onIncrease: () => void
  decreaseHold: HoldHandlers
  increaseHold: HoldHandlers
}

function PriceInput({
  id,
  label,
  value,
  placeholder,
  onValueChange,
  onDecrease,
  onIncrease,
  decreaseHold,
  increaseHold,
}: PriceInputProps) {
  const decreaseDisabled = !value || value <= 0

  return (
    <div className="flex min-w-0 flex-1 flex-col gap-1">
      <Label htmlFor={id} className="text-[10px] font-normal text-muted-foreground">
        {label}
      </Label>
      <div className="relative">
        <Input
          id={id}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          pattern="[0-9]*"
          placeholder={placeholder}
          value={value ?? ''}
          onChange={(event) => {
            const raw = event.target.value.replace(/\D/g, '')
            onValueChange(raw === '' ? null : Number.parseInt(raw, 10))
          }}
          className="h-8 bg-input border-border pr-[4.25rem] pl-2 text-xs tabular-nums placeholder:text-muted-foreground/50"
        />
        <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-1">
          <div className="pointer-events-auto flex gap-0.5">
            <button
              type="button"
              onPointerDown={(event) => decreaseHold.onPointerDown(decreaseDisabled, event)}
              onPointerUp={decreaseHold.onPointerEnd}
              onPointerCancel={decreaseHold.onPointerEnd}
              onLostPointerCapture={decreaseHold.onPointerEnd}
              onKeyDown={(event) => {
                if (event.key !== 'Enter' && event.key !== ' ') return
                event.preventDefault()
                if (!decreaseDisabled) onDecrease()
              }}
              disabled={decreaseDisabled}
              className="select-none rounded p-1 text-muted-foreground transition-colors hover:bg-secondary/80 hover:text-foreground disabled:cursor-default disabled:text-muted-foreground/30"
              aria-label={`Decrease ${label.toLowerCase()} price`}
            >
              <Minus className="h-3 w-3" />
            </button>
            <button
              type="button"
              onPointerDown={(event) => increaseHold.onPointerDown(false, event)}
              onPointerUp={increaseHold.onPointerEnd}
              onPointerCancel={increaseHold.onPointerEnd}
              onLostPointerCapture={increaseHold.onPointerEnd}
              onKeyDown={(event) => {
                if (event.key !== 'Enter' && event.key !== ' ') return
                event.preventDefault()
                onIncrease()
              }}
              className="select-none rounded p-1 text-muted-foreground transition-colors hover:bg-secondary/80 hover:text-foreground"
              aria-label={`Increase ${label.toLowerCase()} price`}
            >
              <Plus className="h-3 w-3" />
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
