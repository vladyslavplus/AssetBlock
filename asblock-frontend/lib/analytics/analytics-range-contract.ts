import { parseDateOnlyUtc } from '@/lib/analytics/analytics-format'
import { ANALYTICS_MAX_DAYS } from '@/lib/analytics/analytics-types'

export function formatDateOnlyUtc(date: Date): string {
  const year = date.getUTCFullYear()
  const month = String(date.getUTCMonth() + 1).padStart(2, '0')
  const day = String(date.getUTCDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function utcTodayDateOnly(): string {
  return formatDateOnlyUtc(new Date())
}

export function addUtcDays(dateOnly: string, days: number): string {
  const parsed = parseDateOnlyUtc(dateOnly)
  if (!parsed) return dateOnly
  parsed.setUTCDate(parsed.getUTCDate() + days)
  return formatDateOnlyUtc(parsed)
}

export function dayNumberUtc(dateOnly: string): number | null {
  const parsed = parseDateOnlyUtc(dateOnly)
  if (!parsed) return null
  return Math.floor(parsed.getTime() / 86_400_000)
}

/**
 * Validates exclusive API range: from < to, span ≤ ANALYTICS_MAX_DAYS,
 * and to not after tomorrow UTC.
 */
export function isValidAnalyticsRange(from: string, to: string): boolean {
  const fromDay = dayNumberUtc(from)
  const toDay = dayNumberUtc(to)
  if (fromDay == null || toDay == null) return false
  if (toDay <= fromDay) return false
  if (toDay - fromDay > ANALYTICS_MAX_DAYS) return false

  const tomorrow = addUtcDays(utcTodayDateOnly(), 1)
  const tomorrowDay = dayNumberUtc(tomorrow)
  if (tomorrowDay == null || toDay > tomorrowDay) return false

  return true
}
