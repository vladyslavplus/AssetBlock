import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { clearAuthCookies } from "@/lib/server/auth-cookies";
import { tryRefreshFromCookies } from "@/lib/server/refresh-session";

/**
 * Proactive refresh: rotates tokens using the httpOnly refresh cookie.
 */
export async function POST() {
  const store = await cookies();
  const tokens = await tryRefreshFromCookies(store);
  if (!tokens) {
    clearAuthCookies(store);
    return NextResponse.json({ ok: false }, { status: 401 });
  }
  return NextResponse.json({ ok: true });
}
