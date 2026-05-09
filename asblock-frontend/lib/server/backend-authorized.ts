import { AUTH_COOKIE_ACCESS } from "@/lib/auth/constants";
import { getServerApiBaseUrl } from "@/lib/api-config";
import { tryRefreshFromCookies } from "@/lib/server/refresh-session";
import type { AuthCookieStore } from "@/lib/server/auth-cookies";

/**
 * Calls the AssetBlock Web API with the access token from cookies; refreshes once on 401.
 */
export async function fetchBackendAuthorized(
  cookieStore: AuthCookieStore,
  path: string,
  init: RequestInit = {},
): Promise<Response> {
  const base = getServerApiBaseUrl();
  const url = path.startsWith("http") ? path : `${base}${path.startsWith("/") ? path : `/${path}`}`;

  let access = cookieStore.get(AUTH_COOKIE_ACCESS)?.value ?? null;
  if (!access) {
    const rotated = await tryRefreshFromCookies(cookieStore);
    access = rotated?.accessToken ?? null;
  }
  if (!access) {
    return new Response(null, { status: 401 });
  }

  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${access}`);
  if (init.body !== undefined && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  let res = await fetch(url, { ...init, headers, cache: "no-store" });
  if (res.status === 401) {
    const rotated = await tryRefreshFromCookies(cookieStore);
    if (!rotated) {
      return res;
    }
    headers.set("Authorization", `Bearer ${rotated.accessToken}`);
    res = await fetch(url, { ...init, headers, cache: "no-store" });
  }
  return res;
}
