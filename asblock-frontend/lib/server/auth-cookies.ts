// eslint-disable-next-line @typescript-eslint/consistent-type-imports -- `typeof cookies` in AuthCookieStore requires this binding
import { cookies } from "next/headers";
import { AUTH_COOKIE_ACCESS, AUTH_COOKIE_REFRESH } from "@/lib/auth/constants";
import type { TokensPayload } from "@/lib/auth/tokens-schema";

/** Use `secure` cookies in production; localhost dev is often HTTP. */
function cookieSecureFlag(): boolean {
  return process.env.NODE_ENV === "production";
}

function maxAgeSecondsFromIso(expiresAtIso: string): number {
  const ms = Date.parse(expiresAtIso) - Date.now();
  // Minimum 60s so short clock skew / near-expiry tokens still get a cookie window.
  return Math.max(60, Math.floor(ms / 1000));
}

/* `cookies` is only referenced via `typeof` for typing; ESLint cannot see the runtime need. */
export type AuthCookieStore = Awaited<ReturnType<typeof cookies>>;

export function setAuthCookies(store: AuthCookieStore, tokens: TokensPayload): void {
  const secure = cookieSecureFlag();
  const accessMaxAge = maxAgeSecondsFromIso(tokens.accessExpiresAt);
  const refreshMaxAge = maxAgeSecondsFromIso(tokens.refreshExpiresAt);

  store.set(AUTH_COOKIE_ACCESS, tokens.accessToken, {
    httpOnly: true,
    secure,
    sameSite: "lax",
    path: "/",
    maxAge: accessMaxAge,
  });
  store.set(AUTH_COOKIE_REFRESH, tokens.refreshToken, {
    httpOnly: true,
    secure,
    sameSite: "lax",
    path: "/",
    maxAge: refreshMaxAge,
  });
}

export function clearAuthCookies(store: AuthCookieStore): void {
  store.delete(AUTH_COOKIE_ACCESS);
  store.delete(AUTH_COOKIE_REFRESH);
}
