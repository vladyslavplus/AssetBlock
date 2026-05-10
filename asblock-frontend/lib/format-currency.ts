/**
 * Shared USD formatting for listing and purchase prices (whole dollars).
 */
const usdWholeFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  maximumFractionDigits: 0,
});

/** Formats a numeric amount as USD with no fractional digits (e.g. "$12"). */
export function formatUsdWhole(amount: number): string {
  return usdWholeFormatter.format(amount);
}
