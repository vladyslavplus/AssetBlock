export const SELL_TABS = [
  'overview',
  'analytics',
  'listings',
  'collections',
  'bundles',
  'upload',
] as const

export type SellTab = (typeof SELL_TABS)[number]

export function parseSellTab(value: string | null | undefined): SellTab {
  if (value && (SELL_TABS as readonly string[]).includes(value)) {
    return value as SellTab
  }
  return 'overview'
}

export function isValidSellTab(value: string): value is SellTab {
  return (SELL_TABS as readonly string[]).includes(value)
}
