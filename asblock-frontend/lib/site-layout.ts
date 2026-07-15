import { cn } from '@/lib/utils'

/** Horizontal padding aligned with `SiteHeader` / `SiteFooter`. */
export const SITE_CONTENT_GUTTER_CLASS = 'px-4 sm:px-6 lg:px-8'

/**
 * Max-width tokens for primary layout columns.
 * - `site`: catalog and any row that should line up with the header (max-w-7xl).
 * - `wide`: library, asset detail, public profiles (max-w-6xl).
 * - `document`: sell hub, docs, long-form (max-w-4xl).
 * - `form`: account settings (max-w-2xl).
 * - `admin`: admin shell (max-w-5xl).
 * - `receipt`: checkout result cards (max-w-lg).
 */
export const SITE_CONTENT_MAX_CLASS = {
  site: 'max-w-7xl',
  wide: 'max-w-6xl',
  document: 'max-w-4xl',
  form: 'max-w-2xl',
  admin: 'max-w-5xl',
  receipt: 'max-w-lg',
} as const

export type SiteContentWidth = keyof typeof SITE_CONTENT_MAX_CLASS

/** Standard inner wrapper: max-width + center + site gutters. */
export function siteShellClass(variant: SiteContentWidth, className?: string) {
  return cn(SITE_CONTENT_MAX_CLASS[variant], 'mx-auto', SITE_CONTENT_GUTTER_CLASS, className)
}
