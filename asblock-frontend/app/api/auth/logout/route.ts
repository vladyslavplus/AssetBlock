import { cookies } from "next/headers";
import { NextResponse } from "next/server";
import { clearAuthCookies } from "@/lib/server/auth-cookies";

export async function POST() {
  const store = await cookies();
  clearAuthCookies(store);
  return NextResponse.json({ ok: true });
}
