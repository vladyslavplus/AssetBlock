import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { fetchBackendAuthorized } from "@/lib/server/backend-authorized";

export async function POST(request: Request, context: { params: Promise<{ assetId: string }> }) {
  const { assetId } = await context.params;
  const store = await cookies();
  const body = await request.text();
  const path = `/api/reviews/assets/${encodeURIComponent(assetId)}/reviews`;
  const res = await fetchBackendAuthorized(store, path, {
    method: "POST",
    body,
    headers: { "Content-Type": "application/json" },
  });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
