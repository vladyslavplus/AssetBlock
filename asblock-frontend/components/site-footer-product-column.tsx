"use client";

import Link from "next/link";
import { useAuth } from "@/components/auth/auth-context";
import { isAdminRole } from "@/lib/auth/roles";

const PRODUCT_LINKS = [
  { label: "Browse assets", href: "/assets" },
  { label: "Sell on AssetBlock", href: "/sell" },
  { label: "Pricing", href: "#" },
  { label: "Changelog", href: "#" },
] as const;

const linkClassName = "text-xs text-muted-foreground hover:text-foreground transition-colors";

export function SiteFooterProductColumn() {
  const { user, status } = useAuth();
  const showAdmin = status === "authenticated" && isAdminRole(user?.role);

  return (
    <div className="flex flex-col gap-3">
      <p className="text-xs font-semibold text-foreground tracking-wider uppercase">Product</p>
      <ul className="flex flex-col gap-2">
        {PRODUCT_LINKS.map((link) => (
          <li key={link.label}>
            <Link href={link.href} className={linkClassName}>
              {link.label}
            </Link>
          </li>
        ))}
        {showAdmin ? (
          <li>
            <Link href="/admin" className={linkClassName}>
              Admin panel
            </Link>
          </li>
        ) : null}
      </ul>
    </div>
  );
}
