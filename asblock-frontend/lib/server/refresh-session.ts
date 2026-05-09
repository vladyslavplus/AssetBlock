import { AUTH_COOKIE_REFRESH } from "@/lib/auth/constants";
import { tokensResponseSchema, type TokensPayload } from "@/lib/auth/tokens-schema";
import { postAuthJson } from "@/lib/server/auth-backend";
import { setAuthCookies, type AuthCookieStore } from "@/lib/server/auth-cookies";

/**
 * Calls Web API refresh and persists rotated tokens in httpOnly cookies.
 */
export async function exchangeRefreshToken(
  store: AuthCookieStore,
  refreshToken: string,
): Promise<TokensPayload | null> {
  const { ok, data } = await postAuthJson("refresh", { refreshToken });
  if (!ok) {
    return null;
  }
  const parsed = tokensResponseSchema.safeParse(data);
  if (!parsed.success) {
    return null;
  }
  setAuthCookies(store, parsed.data);
  return parsed.data;
}

export async function tryRefreshFromCookies(store: AuthCookieStore): Promise<TokensPayload | null> {
  const rt = store.get(AUTH_COOKIE_REFRESH)?.value;
  if (!rt) {
    return null;
  }
  return exchangeRefreshToken(store, rt);
}
