const LOCALE = 'en-US'

const usdWholeFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'USD',
  maximumFractionDigits: 0,
})

const usdCentsFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** Formats a numeric amount as USD with no fractional digits (e.g. "$12"). */
export function formatUsdWhole(amount: number): string {
  return usdWholeFormatter.format(amount)
}

/** Formats integer cents as currency (defaults to USD, e.g. "$12.50"). */
export function formatMoneyCents(cents: number, currency = 'usd'): string {
  if (currency.toLowerCase() !== 'usd') {
    return new Intl.NumberFormat(LOCALE, {
      style: 'currency',
      currency: currency.toUpperCase(),
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(cents / 100)
  }
  return usdCentsFormatter.format(cents / 100)
}

/** Compact currency for chart axes (e.g. $1.2k). */
export function formatCompactMoneyCents(cents: number, currency = 'usd'): string {
  const dollars = cents / 100
  if (currency.toLowerCase() !== 'usd') {
    return new Intl.NumberFormat(LOCALE, {
      style: 'currency',
      currency: currency.toUpperCase(),
      notation: 'compact',
      maximumFractionDigits: 1,
    }).format(dollars)
  }
  if (Math.abs(dollars) >= 1000) {
    return `$${(dollars / 1000).toFixed(dollars >= 10_000 ? 0 : 1)}k`
  }
  return `$${dollars.toFixed(dollars >= 100 ? 0 : 1)}`
}
