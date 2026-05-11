import Link from "next/link";
import { siteShellClass } from "@/lib/site-layout";
import { Github, Twitter } from "lucide-react";
import { BrandLogo } from "@/components/brand-logo";
import { SiteFooterProductColumn } from "@/components/site-footer-product-column";
import { Separator } from "@/components/ui/separator";

const FOOTER_LINKS = {
  Developers: [
    { label: "Documentation", href: "/docs" },
    { label: "API reference", href: "/docs#api" },
    { label: "Status", href: "#" },
  ],
  Legal: [
    { label: "Privacy policy", href: "#" },
    { label: "Terms of service", href: "#" },
    { label: "Licenses", href: "#" },
  ],
};

export function SiteFooter() {
  const year = new Date().getFullYear();

  return (
    <footer className="border-t border-border" aria-label="Site footer">
      <div className={siteShellClass("site", "py-12 sm:py-16")}>
        <div className="grid grid-cols-2 sm:grid-cols-5 gap-8">
          <div className="col-span-2 flex flex-col gap-4">
            <BrandLogo className="w-fit" />
            <p className="text-xs text-muted-foreground leading-relaxed max-w-[200px]">
              The developer-first marketplace for intellectual property assets.
            </p>
            <div className="flex items-center gap-3">
              <Link
                href="#"
                aria-label="GitHub"
                className="text-muted-foreground hover:text-foreground transition-colors"
              >
                <Github className="w-4 h-4" />
              </Link>
              <Link
                href="#"
                aria-label="Twitter"
                className="text-muted-foreground hover:text-foreground transition-colors"
              >
                <Twitter className="w-4 h-4" />
              </Link>
            </div>
          </div>

          <SiteFooterProductColumn />

          {Object.entries(FOOTER_LINKS).map(([group, links]) => (
            <div key={group} className="flex flex-col gap-3">
              <p className="text-xs font-semibold text-foreground tracking-wider uppercase">
                {group}
              </p>
              <ul className="flex flex-col gap-2">
                {links.map((link) => (
                  <li key={link.label}>
                    <Link
                      href={link.href}
                      className="text-xs text-muted-foreground hover:text-foreground transition-colors"
                    >
                      {link.label}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <Separator className="my-8 bg-border" />

        <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
          <p className="text-xs text-muted-foreground">
            &copy; {year} AssetBlock. All rights reserved.
          </p>
          <p className="text-[11px] text-muted-foreground/60 max-w-sm text-right leading-relaxed">
            Marketplace content is user-generated. AssetBlock does not warrant
            the accuracy, completeness, or fitness of any listed asset for any
            particular purpose.
          </p>
        </div>
      </div>
    </footer>
  );
}
