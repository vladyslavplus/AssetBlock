import { AUTH_COOKIE_ACCESS } from "@/lib/auth/constants";
import { getServerApiBaseUrl } from "@/lib/http/api-config";
import { tryRefreshFromCookies } from "@/lib/server/refresh-session";
import type { AuthCookieStore } from "@/lib/server/auth-cookies";

export interface FetchBackendAuthorizedOptions {
  /** Set false when called from a Server Component (cannot mutate cookies). Default true. */
  persistRefreshedTokens?: boolean;
}

/**
 * Calls the AssetBlock Web API with the access token from cookies; refreshes once on 401.
 */
export async function fetchBackendAuthorized(
  cookieStore: AuthCookieStore,
  path: string,
  init: RequestInit = {},
  authOpts: FetchBackendAuthorizedOptions = {},
): Promise<Response> {
  const persistRefreshedTokens = authOpts.persistRefreshedTokens !== false;
  const refreshOpts = { persistCookies: persistRefreshedTokens };

  const base = getServerApiBaseUrl();
  const url = path.startsWith("http") ? path : `${base}${path.startsWith("/") ? path : `/${path}`}`;

  let access = cookieStore.get(AUTH_COOKIE_ACCESS)?.value ?? null;
  if (!access) {
    const rotated = await tryRefreshFromCookies(cookieStore, refreshOpts);
    access = rotated?.accessToken ?? null;
  }
  if (!access) {
    return new Response(null, { status: 401 });
  }

  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${access}`);
  if (init.body !== undefined && !headers.has("Content-Type")) {
    const isFormData = typeof FormData !== "undefined" && init.body instanceof FormData;
    if (!isFormData) {
      headers.set("Content-Type", "application/json");
    }
  }

  let res = await fetch(url, { ...init, headers, cache: "no-store" });
  if (res.status === 401) {
    const rotated = await tryRefreshFromCookies(cookieStore, refreshOpts);
    if (!rotated) {
      return res;
    }
    headers.set("Authorization", `Bearer ${rotated.accessToken}`);
    res = await fetch(url, { ...init, headers, cache: "no-store" });
  }
  return res;
}
