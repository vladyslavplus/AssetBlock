"use client";

import Link from "next/link";
import { useState, useEffect } from "react";
import { Menu, X, Package2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

export function SiteHeader() {
  const [scrolled, setScrolled] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 8);
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

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
              href="#"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Browse assets
            </Link>
            <Link
              href="#"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Sell
            </Link>
            <Link
              href="#"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              Docs
            </Link>
          </nav>

          <div className="hidden md:flex items-center gap-3">
            <Button
              variant="outline"
              size="sm"
              className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth"
            >
              Sign in
            </Button>
            <Button
              size="sm"
              className="bg-primary text-primary-foreground hover:bg-[#6D28D9] shadow-[0_0_16px_rgba(124,58,237,0.25)] hover:shadow-[0_0_20px_rgba(124,58,237,0.35)] transition-smooth"
            >
              Get started
            </Button>
          </div>

          <button
            className="md:hidden p-2 text-muted-foreground hover:text-foreground transition-colors"
            onClick={() => setMenuOpen((v) => !v)}
            aria-label={menuOpen ? "Close menu" : "Open menu"}
            aria-expanded={menuOpen}
          >
            {menuOpen ? <X className="w-5 h-5" /> : <Menu className="w-5 h-5" />}
          </button>
        </div>
      </div>

      {menuOpen && (
        <div className="md:hidden border-t border-border bg-[#07060B]/95 backdrop-blur-md px-4 py-4 flex flex-col gap-4">
          <nav className="flex flex-col gap-3" aria-label="Mobile navigation">
            <Link href="#" className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1">Browse assets</Link>
            <Link href="#" className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1">Sell</Link>
            <Link href="#" className="text-sm text-muted-foreground hover:text-foreground transition-colors py-1">Docs</Link>
          </nav>
          <div className="flex flex-col gap-2 pt-2 border-t border-border">
            <Button variant="outline" className="w-full border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 transition-smooth">Sign in</Button>
            <Button className="w-full bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth">Get started</Button>
          </div>
        </div>
      )}
    </header>
  );
}
