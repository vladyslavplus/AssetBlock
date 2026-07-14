import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function GET() {
  const store = await cookies();
  const qs = new URLSearchParams({
    page: "1",
    pageSize: "100",
    sortDirection: "DESC",
  });
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/purchases?${qs.toString()}`,
    { method: "GET" },
    { persistRefreshedTokens: false },
  );
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
