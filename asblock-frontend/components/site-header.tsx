"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState, useEffect } from "react";
import { ChevronDown, Menu, X, Package2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";
import { useAuth } from "@/components/auth/auth-context";
import { NotificationBell } from "@/components/notifications/notification-bell";

export function SiteHeader() {
  const router = useRouter();
  const { user, status, logout, isAdmin } = useAuth();
  const [scrolled, setScrolled] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);

  const authed = status === "authenticated" && user !== null;
  const authPending = status === "loading";

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 8);
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  async function handleSignOut() {
    setMenuOpen(false);
    await logout();
    router.refresh();
  }

  return (
    <header
      className={cn(
        "fixed top-0 left-0 right-0 z-50",
        "border-b border-solid transition-[background-color,border-color,backdrop-filter] duration-300 ease-out",
        scrolled
          ? "border-border bg-[#07060B]/80 backdrop-blur-md"
          : "border-transparent bg-[#07060B]/0 backdrop-blur-none",
      )}
    >
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between h-16">
          <Link
            href="/"
            className="flex items-center gap-2 text-foreground hover:text-foreground/90 transition-colors"
          >
            <div className="flex items-center justify-center w-8 h-8 rounded-md bg-primary/20 border border-primary/30">
              <Package2 className="w-4 h-4 text-primary" />
            </div>
            <span className="font-semibold text-base tracking-tight">
              AssetBlock
            </span>
          </Link>

          <nav className="hidden md:flex items-center gap-8" aria-label="Main navigation">
            <Link
              href="/assets"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Browse assets
            </Link>
            <Link
              href="/library"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Library
            </Link>
            <Link
              href="/sell"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Sell
            </Link>
            <Link
              href="/docs"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Docs
            </Link>
          </nav>

          <div className="flex items-center gap-2 md:gap-3">
            {authPending ? (
              <div
                className="h-8 w-28 rounded-md bg-muted/25 animate-pulse hidden md:block"
                aria-hidden
              />
            ) : authed ? (
              <>
                <NotificationBell />
                <div className="hidden md:block">
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <Button
                        variant="outline"
                        size="sm"
                        type="button"
                        className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth gap-1"
                      >
                        Account
                        <ChevronDown className="size-3.5 opacity-70" aria-hidden />
                      </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end" className="min-w-[10rem]">
                      <DropdownMenuItem asChild>
                        <Link href="/account">Account</Link>
                      </DropdownMenuItem>
                      {isAdmin ? (
                        <DropdownMenuItem asChild>
                          <Link href="/admin">Admin panel</Link>
                        </DropdownMenuItem>
                      ) : null}
                      <DropdownMenuSeparator />
                      <DropdownMenuItem
                        variant="destructive"
                        onSelect={() => void handleSignOut()}
                      >
                        Sign out
                      </DropdownMenuItem>
                    </DropdownMenuContent>
                  </DropdownMenu>
                </div>
              </>
            ) : (
              <div className="hidden md:flex items-center gap-3">
                <Button
                  variant="outline"
                  size="sm"
                  asChild
                  className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth"
                >
                  <Link href="/login">Sign in</Link>
                </Button>
                <Button
                  size="sm"
                  asChild
                  className="bg-primary text-primary-foreground hover:bg-[#6D28D9] shadow-[0_0_16px_rgba(124,58,237,0.25)] hover:shadow-[0_0_20px_rgba(124,58,237,0.35)] transition-smooth"
                >
                  <Link href="/register">Get started</Link>
                </Button>
              </div>
            )}
            <button
              className="md:hidden p-2 text-muted-foreground hover:text-foreground transition-colors"
              type="button"
              onClick={() => setMenuOpen((v) => !v)}
              aria-label={menuOpen ? "Close menu" : "Open menu"}
              aria-expanded={menuOpen}
            >
              {menuOpen ? <X className="w-5 h-5" /> : <Menu className="w-5 h-5" />}
            </button>
          </div>
        </div>
      </div>

      {menuOpen && (
        <div className="md:hidden border-t border-border bg-[#07060B]/95 backdrop-blur-md px-4 py-4 flex flex-col gap-4">
          <nav className="flex flex-col gap-3" aria-label="Mobile navigation">
            <Link
              href="/assets"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1"
              onClick={() => setMenuOpen(false)}
            >
              Browse assets
            </Link>
            <Link
              href="/library"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1"
              onClick={() => setMenuOpen(false)}
            >
              Library
            </Link>
            <Link
              href="/sell"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1"
              onClick={() => setMenuOpen(false)}
            >
              Sell
            </Link>
            <Link
              href="/docs"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1"
              onClick={() => setMenuOpen(false)}
            >
              Docs
            </Link>
          </nav>
          <div className="flex flex-col gap-2 pt-2 border-t border-border">
            {authPending ? (
              <div className="h-9 w-full rounded-md bg-muted/25 animate-pulse" aria-hidden />
            ) : authed ? (
              <div className="flex flex-col gap-2">
                {isAdmin ? (
                  <Button
                    variant="outline"
                    asChild
                    className="w-full border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 transition-smooth"
                  >
                    <Link href="/admin" onClick={() => setMenuOpen(false)}>
                      Admin panel
                    </Link>
                  </Button>
                ) : null}
                <Button variant="outline" asChild className="w-full border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 transition-smooth">
                  <Link href="/account" onClick={() => setMenuOpen(false)}>
                    Account
                  </Link>
                </Button>
                <Button
                  variant="outline"
                  type="button"
                  className="w-full border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 transition-smooth"
                  onClick={() => void handleSignOut()}
                >
                  Sign out
                </Button>
              </div>
            ) : (
              <>
                <Button variant="outline" asChild className="w-full border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 transition-smooth">
                  <Link href="/login" onClick={() => setMenuOpen(false)}>Sign in</Link>
                </Button>
                <Button asChild className="w-full bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth">
                  <Link href="/register" onClick={() => setMenuOpen(false)}>Get started</Link>
                </Button>
              </>
            )}
          </div>
        </div>
      )}
    </header>
  );
}
