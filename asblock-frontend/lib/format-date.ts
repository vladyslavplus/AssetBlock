/** Locale for catalog/profile copy (kept consistent across the storefront). */
const LOCALE = "en-US";

const longDateFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: "numeric",
  month: "long",
  day: "numeric",
});

const longMonthYearFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: "numeric",
  month: "long",
});

const shortMonthDateFormatter = new Intl.DateTimeFormat(LOCALE, {
  year: "numeric",
  month: "short",
  day: "numeric",
});

function toDate(value: Date | string | number): Date {
  return value instanceof Date ? value : new Date(value);
}

/** e.g. "January 15, 2026" */
export function formatLongDate(value: Date | string | number): string {
  return longDateFormatter.format(toDate(value));
}

/** e.g. "January 2026" — member since, periods without day */
export function formatLongMonthYear(value: Date | string | number): string {
  return longMonthYearFormatter.format(toDate(value));
}

/** e.g. "Jan 15, 2026" — compact rows (reviews, activity) */
export function formatShortMonthDate(value: Date | string | number): string {
  return shortMonthDateFormatter.format(toDate(value));
}
