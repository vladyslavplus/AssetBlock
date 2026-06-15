import { NextResponse } from "next/server";
import { getServerApiBaseUrl } from "@/lib/http/api-config";

export async function GET() {
  const base = getServerApiBaseUrl();
  const res = await fetch(`${base}/api/users/social-platforms`, { cache: "no-store" });
  const text = await res.text();
  const contentType = res.headers.get("Content-Type") ?? "application/json";
  return new NextResponse(text.length > 0 ? text : null, {
    status: res.status,
    headers: { "Content-Type": contentType },
  });
}
