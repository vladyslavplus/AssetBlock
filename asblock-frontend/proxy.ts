import type { NextRequest } from "next/server";
import { NextResponse } from "next/server";
import { AUTH_COOKIE_REFRESH } from "@/lib/auth/constants";

export function proxy(request: NextRequest) {
  const hasRefresh = Boolean(request.cookies.get(AUTH_COOKIE_REFRESH)?.value);
  const { pathname } = request.nextUrl;

  const needsAuth =
    pathname.startsWith("/library") || pathname.startsWith("/account");

  if (needsAuth && !hasRefresh) {
    const url = request.nextUrl.clone();
    url.pathname = "/login";
    url.searchParams.set("returnUrl", `${pathname}${request.nextUrl.search}`);
    return NextResponse.redirect(url);
  }

  return NextResponse.next();
}

export const config = {
  matcher: ["/library", "/library/:path*", "/account", "/account/:path*"],
};
