import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function GET(request: Request) {
  const store = await cookies();
  const url = new URL(request.url);
  const qs = url.searchParams.toString();
  const path = `/api/users/me/notifications${qs ? `?${qs}` : ""}`;
  const res = await fetchBackendAuthorized(store, path, { method: "GET" });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
