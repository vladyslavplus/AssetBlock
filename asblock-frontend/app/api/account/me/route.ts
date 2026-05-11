import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function GET() {
  const store = await cookies();
  const res = await fetchBackendAuthorized(store, "/api/users/me", { method: "GET" });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}

export async function PATCH(request: Request) {
  const store = await cookies();
  let body: string;
  try {
    body = await request.text();
  } catch {
    return NextResponse.json({ errors: [{ identifier: "body", message: "Invalid body" }] }, { status: 400 });
  }
  const res = await fetchBackendAuthorized(store, "/api/users/me", {
    method: "PATCH",
    body,
  });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
