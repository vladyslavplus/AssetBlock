/**
 * httpOnly cookie names for BFF auth (readable by Next.js proxy for coarse guards).
 * Values are never exposed to client JS.
 */
export const AUTH_COOKIE_ACCESS = 'assetblock_at'
export const AUTH_COOKIE_REFRESH = 'assetblock_rt'
