'use client'

import { useEffect, useState } from 'react'
import type { DateRange } from 'react-day-picker'
import { CalendarIcon } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { Calendar } from '@/components/ui/calendar'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import {
  ANALYTICS_RANGE_PRESETS,
  type AnalyticsRangePreset,
  type AnalyticsUrlState,
} from '@/lib/analytics/analytics-types'
import {
  formatAnalyticsRangeLabel,
  rangePresetLabel,
  resolveAnalyticsUtcRange,
  validateCustomInclusiveRange,
} from '@/lib/analytics/analytics-range'
import { utcTodayDateOnly } from '@/lib/analytics/analytics-range-contract'
import { formatUtcDateOnlyLong, parseDateOnlyUtc } from '@/lib/analytics/analytics-format'
import { cn } from '@/lib/utils'

interface AnalyticsRangePickerProps {
  state: AnalyticsUrlState
  onChange: (next: Partial<AnalyticsUrlState>) => void
}

/** Local calendar Date with the same Y-M-D numbers as a UTC date-only string. */
function calendarDateFromDateOnly(dateOnly: string): Date | undefined {
  const parsed = parseDateOnlyUtc(dateOnly)
  if (!parsed) return undefined
  return new Date(parsed.getUTCFullYear(), parsed.getUTCMonth(), parsed.getUTCDate())
}

/** Read DayPicker local calendar date as YYYY-MM-DD (date-only, not UTC instant). */
function dateOnlyFromCalendarDate(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function parseCustomRange(state: AnalyticsUrlState): DateRange | undefined {
  if (!state.customFrom) return undefined
  const from = calendarDateFromDateOnly(state.customFrom)
  const to = state.customTo ? calendarDateFromDateOnly(state.customTo) : undefined
  if (!from) return undefined
  return { from, to }
}

/** UTC "today" as a local calendar date for DayPicker disabled bounds. */
function utcTodayAsLocalCalendarDate(): Date {
  const today = utcTodayDateOnly()
  const [year, month, day] = today.split('-').map(Number)
  return new Date(year, month - 1, day)
}

export function AnalyticsRangePicker({ state, onChange }: AnalyticsRangePickerProps) {
  const utcRange = resolveAnalyticsUtcRange(state)
  const rangeLabel = formatAnalyticsRangeLabel(state, utcRange)
  const [customOpen, setCustomOpen] = useState(false)
  const [draftRange, setDraftRange] = useState<DateRange | undefined>(() => parseCustomRange(state))
  const [applyError, setApplyError] = useState<string | null>(null)
  const [calendarMonths, setCalendarMonths] = useState(2)

  useEffect(() => {
    const media = window.matchMedia('(min-width: 768px)')
    const update = () => setCalendarMonths(media.matches ? 2 : 1)
    update()
    media.addEventListener('change', update)
    return () => media.removeEventListener('change', update)
  }, [])

  const handleCustomOpenChange = (open: boolean) => {
    setCustomOpen(open)
    if (open) {
      setDraftRange(parseCustomRange(state))
      setApplyError(null)
    }
  }

  let customSummary = 'Pick a date range'
  if (draftRange?.from) {
    const from = formatUtcDateOnlyLong(dateOnlyFromCalendarDate(draftRange.from))
    if (!draftRange.to) {
      customSummary = `${from} – …`
    } else {
      const to = formatUtcDateOnlyLong(dateOnlyFromCalendarDate(draftRange.to))
      customSummary = `${from} – ${to}`
    }
  }

  function applyPreset(preset: Exclude<AnalyticsRangePreset, 'custom'>) {
    onChange({
      range: preset,
      customFrom: null,
      customTo: null,
      page: 1,
    })
  }

  function applyCustomRange() {
    if (!draftRange?.from || !draftRange.to) return
    const from = dateOnlyFromCalendarDate(draftRange.from)
    const to = dateOnlyFromCalendarDate(draftRange.to)
    const validation = validateCustomInclusiveRange(from, to)
    if (!validation.ok) {
      setApplyError(validation.message)
      return
    }
    onChange({
      range: 'custom',
      customFrom: from,
      customTo: to,
      page: 1,
    })
    setApplyError(null)
    setCustomOpen(false)
  }

  const presetValue = state.range === 'custom' ? undefined : state.range

  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex flex-wrap items-center gap-1">
        <ToggleGroup
          type="single"
          value={presetValue}
          onValueChange={(value) => {
            if (!value || value === 'custom') return
            applyPreset(value as Exclude<AnalyticsRangePreset, 'custom'>)
          }}
          className="flex flex-wrap justify-start gap-1"
          aria-label="Analytics date range"
        >
          {ANALYTICS_RANGE_PRESETS.filter((preset) => preset !== 'custom').map((preset) => (
            <ToggleGroupItem
              key={preset}
              value={preset}
              className="text-xs sm:text-sm"
              aria-label={rangePresetLabel(preset)}
            >
              {rangePresetLabel(preset)}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>

        <Popover open={customOpen} onOpenChange={handleCustomOpenChange}>
          <PopoverTrigger asChild>
            <Button
              type="button"
              variant={state.range === 'custom' ? 'secondary' : 'outline'}
              size="sm"
              className={cn('text-xs sm:text-sm', state.range === 'custom' && 'bg-accent')}
              aria-label="Custom date range"
              aria-pressed={state.range === 'custom'}
            >
              <CalendarIcon className="mr-1.5 size-3.5" aria-hidden />
              Custom
            </Button>
          </PopoverTrigger>
          <PopoverContent className="w-auto p-0" align="start">
            <div className="space-y-3 p-3">
              <p className="text-sm font-medium">Custom range (UTC, inclusive end)</p>
              <Calendar
                mode="range"
                selected={draftRange}
                onSelect={(range) => {
                  setDraftRange(range)
                  setApplyError(null)
                }}
                disabled={{ after: utcTodayAsLocalCalendarDate() }}
                numberOfMonths={calendarMonths}
                className="rounded-md border"
              />
              <p className="text-xs text-muted-foreground">{customSummary}</p>
              {applyError ? (
                <p className="text-xs text-destructive" role="alert">
                  {applyError}
                </p>
              ) : null}
              <Button
                type="button"
                size="sm"
                className="w-full"
                disabled={!draftRange?.from || !draftRange?.to}
                onClick={applyCustomRange}
              >
                Apply range
              </Button>
            </div>
          </PopoverContent>
        </Popover>
      </div>

      <p className="text-xs text-muted-foreground sm:text-right">
        <span className="font-medium text-foreground">{rangeLabel}</span>
      </p>
    </div>
  )
}
