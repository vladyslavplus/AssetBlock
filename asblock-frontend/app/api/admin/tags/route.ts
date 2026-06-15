import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function POST(request: Request) {
  const store = await cookies();
  const body = await request.text();
  const res = await fetchBackendAuthorized(store, "/api/tags", {
    method: "POST",
    body,
  });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
