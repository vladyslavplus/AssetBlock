import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

/**
 * Proxies GET /api/users/me/assets (seller's listed assets) with session cookies.
 */
export async function GET(request: Request) {
  const store = await cookies();
  const url = new URL(request.url);
  const qs = url.searchParams.toString();
  const backendPath = `/api/users/me/assets${qs ? `?${qs}` : ""}`;
  const res = await fetchBackendAuthorized(store, backendPath, { method: "GET" });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
