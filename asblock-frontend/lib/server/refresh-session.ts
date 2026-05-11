import { AUTH_COOKIE_REFRESH } from "@/lib/auth/constants";
import { tokensResponseSchema, type TokensPayload } from "@/lib/auth/tokens-schema";
import { postAuthJson } from "@/lib/server/auth-backend";
import { setAuthCookies, type AuthCookieStore } from "@/lib/server/auth-cookies";

export interface RefreshSessionOptions {
  /**
   * When false, returns new tokens but does not call `cookies().set` (required from Server Components).
   * Route Handlers / Server Actions should keep the default (persist).
   */
  persistCookies?: boolean;
}

/**
 * Calls Web API refresh and optionally persists rotated tokens in httpOnly cookies.
 */
export async function exchangeRefreshToken(
  store: AuthCookieStore,
  refreshToken: string,
  options: RefreshSessionOptions = {},
): Promise<TokensPayload | null> {
  const persistCookies = options.persistCookies !== false;
  const { ok, data } = await postAuthJson("refresh", { refreshToken });
  if (!ok) {
    return null;
  }
  const parsed = tokensResponseSchema.safeParse(data);
  if (!parsed.success) {
    return null;
  }
  if (persistCookies) {
    setAuthCookies(store, parsed.data);
  }
  return parsed.data;
}

export async function tryRefreshFromCookies(
  store: AuthCookieStore,
  options: RefreshSessionOptions = {},
): Promise<TokensPayload | null> {
  const rt = store.get(AUTH_COOKIE_REFRESH)?.value;
  if (!rt) {
    return null;
  }
  return exchangeRefreshToken(store, rt, options);
}
