import type { Route } from 'next'

/**
 * Sanitizes an internal application return URL.
 * Rejects external URLs, protocol-relative URLs (`//evil.com`), and non-path inputs.
 */
export function sanitizeInternalReturnUrl(url: string | null | undefined): string | null {
  if (!url || typeof url !== 'string') return null
  const trimmed = url.trim()
  if (!trimmed.startsWith('/') || trimmed.startsWith('//') || trimmed.startsWith('/\\')) {
    return null
  }
  // Disallow control characters or newlines
  if (/[\r\n\t]/.test(trimmed)) {
    return null
  }
  return trimmed
}

export const routes = {
  home: () => '/' as Route,
  assets: (params?: { category?: string; query?: string; page?: number }) => {
    const sp = new URLSearchParams()
    if (params?.category) sp.set('category', params.category)
    if (params?.query) sp.set('query', params.query)
    if (params?.page && params.page > 1) sp.set('page', String(params.page))
    const qs = sp.toString()
    return (qs ? `/assets?${qs}` : '/assets') as Route
  },
  assetDetail: (id: string) => `/assets/${encodeURIComponent(id)}` as Route,
  assetDownload: (id: string, versionId?: string | null) =>
    (versionId?.trim()
      ? `/api/assets/${encodeURIComponent(id)}/download?versionId=${encodeURIComponent(versionId.trim())}`
      : `/api/assets/${encodeURIComponent(id)}/download`) as Route,
  bundles: (params?: { query?: string; page?: number }) => {
    const sp = new URLSearchParams()
    if (params?.query) sp.set('query', params.query)
    if (params?.page && params.page > 1) sp.set('page', String(params.page))
    const qs = sp.toString()
    return (qs ? `/bundles?${qs}` : '/bundles') as Route
  },
  bundleDetail: (id: string) => `/bundles/${encodeURIComponent(id)}` as Route,
  collections: (params?: { query?: string; page?: number }) => {
    const sp = new URLSearchParams()
    if (params?.query) sp.set('query', params.query)
    if (params?.page && params.page > 1) sp.set('page', String(params.page))
    const qs = sp.toString()
    return (qs ? `/collections?${qs}` : '/collections') as Route
  },
  collectionDetail: (id: string) => `/collections/${encodeURIComponent(id)}` as Route,
  userProfile: (username: string, page?: number) => {
    const base = `/users/${encodeURIComponent(username)}`
    if (page && page > 1) {
      return `${base}?page=${page}` as Route
    }
    return base as Route
  },
  library: () => '/library' as Route,
  account: () => '/account' as Route,
  sell: () => '/sell' as Route,
  sellerAssetEdit: (id: string) => `/sell/assets/${encodeURIComponent(id)}/edit` as Route,
  sellerAssetAnalytics: (id: string) => `/sell/analytics/assets/${encodeURIComponent(id)}` as Route,
  sellerBundleAnalytics: (id: string) =>
    `/sell/analytics/bundles/${encodeURIComponent(id)}` as Route,
  login: (returnUrl?: string | null) => {
    const safe = sanitizeInternalReturnUrl(returnUrl)
    if (!safe) return '/login' as Route
    const sp = new URLSearchParams({ returnUrl: safe })
    return `/login?${sp.toString()}` as Route
  },
  register: (returnUrl?: string | null) => {
    const safe = sanitizeInternalReturnUrl(returnUrl)
    if (!safe) return '/register' as Route
    const sp = new URLSearchParams({ returnUrl: safe })
    return `/register?${sp.toString()}` as Route
  },
  forgotPassword: () => '/forgot-password' as Route,
  resetPassword: (token?: string | null) => {
    if (!token) return '/reset-password' as Route
    const sp = new URLSearchParams({ token })
    return `/reset-password?${sp.toString()}` as Route
  },
  verifyEmail: (token?: string | null) => {
    if (!token) return '/verify-email' as Route
    const sp = new URLSearchParams({ token })
    return `/verify-email?${sp.toString()}` as Route
  },
  confirmEmailChange: (token?: string | null) => {
    if (!token) return '/confirm-email-change' as Route
    const sp = new URLSearchParams({ token })
    return `/confirm-email-change?${sp.toString()}` as Route
  },
  checkoutSuccess: () => '/checkout/success' as Route,
  checkoutCancel: () => '/checkout/cancel' as Route,
} as const
