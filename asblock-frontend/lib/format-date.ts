import { formatDistanceToNow } from 'date-fns'

/** Locale for catalog/profile copy (kept consistent across the storefront). */
const LOCALE = 'en-US'

const longDateFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'long',
  day: 'numeric',
})

const longMonthYearFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'long',
})

const shortMonthDateFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
})

const localDateTimeFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

function toDate(value: Date | string | number): Date {
  return value instanceof Date ? value : new Date(value)
}

/** e.g. "January 15, 2026" */
export function formatLongDate(value: Date | string | number): string {
  return longDateFormatter.format(toDate(value))
}

/** e.g. "January 2026" — member since, periods without day */
export function formatLongMonthYear(value: Date | string | number): string {
  return longMonthYearFormatter.format(toDate(value))
}

/** e.g. "Jan 15, 2026" — compact rows (reviews, activity) */
export function formatShortMonthDate(value: Date | string | number): string {
  return shortMonthDateFormatter.format(toDate(value))
}

/** e.g. "Jan 15, 2026, 10:30:00 AM" — local audit logs & detailed timestamps */
export function formatDateTimeLocal(value: Date | string | number): string {
  try {
    const d = toDate(value)
    if (Number.isNaN(d.getTime())) {
      return String(value)
    }
    return localDateTimeFormatter.format(d)
  } catch {
    return String(value)
  }
}

/** e.g. "5 minutes ago", "in 2 hours" — relative notification timestamps */
export function formatRelativeTime(value: Date | string | number): string {
  try {
    const d = toDate(value)
    if (Number.isNaN(d.getTime())) {
      return String(value)
    }
    return formatDistanceToNow(d, { addSuffix: true })
  } catch {
    return String(value)
  }
}
