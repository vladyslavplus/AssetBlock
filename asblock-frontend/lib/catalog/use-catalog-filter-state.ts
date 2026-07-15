'use client'

import { useCallback, useEffect, useState } from 'react'

import { usePointerStepRepeat } from '@/hooks/use-pointer-step-repeat'
import type { CatalogFilters } from '@/lib/catalog/catalog-filters'

const FILTER_INPUT_DEBOUNCE_MS = 300

function clampPricePair(
  min: number | null,
  max: number | null,
): { min: number | null; max: number | null } {
  let nextMin = min
  const nextMax = max === 0 ? null : max
  if (nextMin !== null && nextMax !== null && nextMin > nextMax) {
    nextMin = Math.max(0, nextMax - 1)
  }
  return { min: nextMin, max: nextMax }
}

interface UseCatalogFilterStateOptions {
  filters: CatalogFilters
  onFilterChange: (updates: Partial<CatalogFilters>) => void
}

export function useCatalogFilterState({ filters, onFilterChange }: UseCatalogFilterStateOptions) {
  const [searchInput, setSearchInput] = useState(filters.search)
  const [draftMin, setDraftMin] = useState<number | null>(filters.minPrice)
  const [draftMax, setDraftMax] = useState<number | null>(filters.maxPrice)

  useEffect(() => {
    queueMicrotask(() => setSearchInput(filters.search))
  }, [filters.search])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      if (searchInput !== filters.search) {
        onFilterChange({ search: searchInput, page: 1 })
      }
    }, FILTER_INPUT_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [searchInput, filters.search, onFilterChange])

  useEffect(() => {
    queueMicrotask(() => {
      setDraftMin(filters.minPrice)
      setDraftMax(filters.maxPrice)
    })
  }, [filters.minPrice, filters.maxPrice])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const clamped = clampPricePair(draftMin, draftMax)
      if (clamped.min !== draftMin || clamped.max !== draftMax) {
        setDraftMin(clamped.min)
        setDraftMax(clamped.max)
      }
      if (clamped.min !== filters.minPrice || clamped.max !== filters.maxPrice) {
        onFilterChange({ minPrice: clamped.min, maxPrice: clamped.max, page: 1 })
      }
    }, FILTER_INPUT_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [draftMin, draftMax, filters.minPrice, filters.maxPrice, onFilterChange])

  const applyDraftPrices = useCallback((min: number | null, max: number | null) => {
    const clamped = clampPricePair(min, max)
    setDraftMin(clamped.min)
    setDraftMax(clamped.max)
  }, [])

  const adjustPrice = useCallback(
    (field: 'min' | 'max', delta: number) => {
      if (field === 'min') {
        applyDraftPrices(Math.max(0, (draftMin ?? 0) + delta), draftMax)
      } else {
        applyDraftPrices(draftMin, Math.max(0, (draftMax ?? 0) + delta))
      }
    },
    [draftMin, draftMax, applyDraftPrices],
  )

  const minMinusHold = usePointerStepRepeat(
    useCallback(() => adjustPrice('min', -1), [adjustPrice]),
  )
  const minPlusHold = usePointerStepRepeat(useCallback(() => adjustPrice('min', 1), [adjustPrice]))
  const maxMinusHold = usePointerStepRepeat(
    useCallback(() => adjustPrice('max', -1), [adjustPrice]),
  )
  const maxPlusHold = usePointerStepRepeat(useCallback(() => adjustPrice('max', 1), [adjustPrice]))

  return {
    searchInput,
    setSearchInput,
    draftMin,
    draftMax,
    applyDraftPrices,
    adjustPrice,
    minMinusHold,
    minPlusHold,
    maxMinusHold,
    maxPlusHold,
  }
}

export type CatalogFilterState = ReturnType<typeof useCatalogFilterState>
