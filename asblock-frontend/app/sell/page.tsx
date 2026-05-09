import Link from "next/link";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { Button } from "@/components/ui/button";
import { ArrowRight, Store, Upload, ShieldCheck } from "lucide-react";

export default function SellPage() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-3xl mx-auto px-4 sm:px-6 lg:px-8 py-12">
          <p className="text-xs font-mono text-accent tracking-widest uppercase mb-3">
            For creators
          </p>
          <h1 className="text-3xl sm:text-4xl font-semibold text-balance mb-4">
            Sell on AssetBlock
          </h1>
          <p className="text-muted-foreground leading-relaxed mb-10">
            List templates, starter kits, CLI tools, and other digital products. This page is a
            lightweight overview; upload and payouts connect through your account once the seller
            dashboard is wired to the API.
          </p>

          <ul className="space-y-6 mb-10">
            <li className="flex gap-3">
              <Store className="w-5 h-5 text-primary shrink-0 mt-0.5" aria-hidden />
              <div>
                <p className="font-medium text-foreground">Reach buyers</p>
                <p className="text-sm text-muted-foreground mt-1">
                  Your assets appear in catalog search alongside the rest of the marketplace.
                </p>
              </div>
            </li>
            <li className="flex gap-3">
              <Upload className="w-5 h-5 text-primary shrink-0 mt-0.5" aria-hidden />
              <div>
                <p className="font-medium text-foreground">Upload &amp; deliver</p>
                <p className="text-sm text-muted-foreground mt-1">
                  Encrypted file delivery after purchase (handled by the platform).
                </p>
              </div>
            </li>
            <li className="flex gap-3">
              <ShieldCheck className="w-5 h-5 text-primary shrink-0 mt-0.5" aria-hidden />
              <div>
                <p className="font-medium text-foreground">Secure checkout</p>
                <p className="text-sm text-muted-foreground mt-1">
                  Buyers pay through integrated checkout; you focus on quality assets.
                </p>
              </div>
            </li>
          </ul>

          <div className="flex flex-wrap gap-3">
            <Button
              asChild
              className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth"
            >
              <Link href="/register">
                Create seller account
                <ArrowRight className="w-4 h-4 ml-2" />
              </Link>
            </Button>
            <Button variant="outline" asChild className="border-border bg-transparent">
              <Link href="/assets">Browse marketplace</Link>
            </Button>
          </div>
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
