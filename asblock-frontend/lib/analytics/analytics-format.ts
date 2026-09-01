const LOCALE = 'en-US'

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/

const utcShortDateFormatter = new Intl.DateTimeFormat(LOCALE, {
  timeZone: 'UTC',
  year: 'numeric',
  month: 'short',
  day: 'numeric',
})

const utcLongDateFormatter = new Intl.DateTimeFormat(LOCALE, {
  timeZone: 'UTC',
  year: 'numeric',
  month: 'long',
  day: 'numeric',
})

const utcDateTimeFormatter = new Intl.DateTimeFormat(LOCALE, {
  timeZone: 'UTC',
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})

export function parseDateOnlyUtc(dateOnly: string): Date | null {
  const match = DATE_ONLY.exec(dateOnly)
  if (!match) return null
  const year = Number(match[1])
  const month = Number(match[2])
  const day = Number(match[3])
  const parsed = new Date(Date.UTC(year, month - 1, day))
  if (
    parsed.getUTCFullYear() !== year ||
    parsed.getUTCMonth() !== month - 1 ||
    parsed.getUTCDate() !== day
  ) {
    return null
  }
  return parsed
}

export { formatMoneyCents, formatCompactMoneyCents } from '@/lib/format-currency'

/** Formats a 0–1 rate as a percentage string. */
export function formatRatePercent(rate: number | null | undefined): string {
  if (rate == null) return '—'
  return `${(rate * 100).toFixed(1)}%`
}

/** Formats nullable period-over-period percentage change. */
export function formatPercentageChange(value: number | null | undefined): string | null {
  if (value == null) return null
  const sign = value > 0 ? '+' : ''
  return `${sign}${value.toFixed(1)}%`
}

export function formatCount(value: number): string {
  return value.toLocaleString(LOCALE)
}

export function formatUtcShortDate(isoOrDateOnly: string): string {
  if (DATE_ONLY.test(isoOrDateOnly)) {
    const parsed = parseDateOnlyUtc(isoOrDateOnly)
    if (!parsed) return isoOrDateOnly
    return utcShortDateFormatter.format(parsed)
  }

  const timestamp = new Date(isoOrDateOnly)
  if (Number.isNaN(timestamp.getTime())) return isoOrDateOnly
  return utcShortDateFormatter.format(timestamp)
}

export function formatUtcDateOnly(dateOnly: string): string {
  return formatUtcShortDate(dateOnly)
}

export function formatUtcDateOnlyLong(dateOnly: string): string {
  const parsed = parseDateOnlyUtc(dateOnly)
  if (!parsed) return dateOnly
  return utcLongDateFormatter.format(parsed)
}

export function formatUtcDateTime(iso: string): string {
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return iso
  return utcDateTimeFormatter.format(parsed)
}

export function formatRating(value: number | null | undefined): string {
  if (value == null) return '—'
  return value.toFixed(1)
}
