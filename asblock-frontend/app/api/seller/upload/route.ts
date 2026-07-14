import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

/**
 * Proxies multipart POST to AssetBlock POST /api/assets/upload (Bearer from cookies).
 */
export async function POST(request: Request) {
  const store = await cookies();
  const formData = await request.formData();
  const res = await fetchBackendAuthorized(store, "/api/assets/upload", {
    method: "POST",
    body: formData,
  });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
