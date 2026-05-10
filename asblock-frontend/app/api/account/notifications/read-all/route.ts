import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function POST() {
  const store = await cookies();
  const res = await fetchBackendAuthorized(store, "/api/users/me/notifications/read-all", {
    method: "POST",
  });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
