import type { Metadata } from "next";
import { cookies } from "next/headers";
import { Button } from "@/components/ui/button";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import Link from "next/link";
import { Download, ExternalLink } from "lucide-react";
import { formatUsdWhole } from "@/lib/format-currency";
import { fetchMyPurchasesFromBackend } from "@/lib/server/library-purchases";
import type { PurchaseLibraryItem } from "@/lib/purchase-types";

export const metadata: Metadata = {
  title: "My library - AssetBlock",
  description: "View and manage your digital asset purchases.",
};

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "long",
  day: "numeric",
});

function PurchaseCard({ purchase }: { purchase: PurchaseLibraryItem }) {
  return (
    <div className="bg-card-elevated border border-border rounded-xl p-4 space-y-3">
      <h2 className="font-semibold text-foreground line-clamp-2">{purchase.assetTitle}</h2>

      <div className="flex items-center gap-2">
        <span className="text-xs text-muted-foreground">
          by{" "}
          <span className="font-mono text-muted-foreground/80">@{purchase.authorUsername}</span>
        </span>
      </div>

      <div className="flex items-center justify-between text-xs text-muted-foreground">
        <span className="font-semibold text-foreground">{formatUsdWhole(Number(purchase.price))}</span>
        <span>{dateFormatter.format(new Date(purchase.purchasedAt))}</span>
      </div>

      <div className="flex gap-2 pt-2 border-t border-border/30">
        <Button
          disabled
          size="sm"
          className="flex-1 bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium text-xs h-8"
          title="Download will be available once the file proxy route is wired."
        >
          <Download className="w-3.5 h-3.5 mr-1.5" />
          Download
        </Button>
        <Button
          asChild
          variant="outline"
          size="sm"
          className="flex-1 border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth font-medium text-xs h-8"
        >
          <Link href={`/assets/${purchase.assetId}`}>
            <ExternalLink className="w-3.5 h-3.5 mr-1.5" />
            View asset
          </Link>
        </Button>
      </div>
    </div>
  );
}

export default async function LibraryPage() {
  const cookieStore = await cookies();
  const result = await fetchMyPurchasesFromBackend(cookieStore);

  const purchases = result.ok ? result.data.items : [];

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="mb-8">
            <h1 className="text-3xl font-bold text-foreground mb-2">My library</h1>
            <p className="text-sm text-muted-foreground">Your purchased digital assets</p>
          </div>

          {!result.ok && (
            <div
              className="mb-6 rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
              role="alert"
            >
              <p className="font-medium">Could not load your library</p>
              <p className="mt-1 text-destructive/90">{result.message}</p>
              {result.status === 401 && (
                <Button asChild variant="outline" size="sm" className="mt-3 border-destructive/50 text-destructive">
                  <Link href="/login?returnUrl=/library">Sign in again</Link>
                </Button>
              )}
            </div>
          )}

          {result.ok && purchases.length > 0 && (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
              {purchases.map((purchase) => (
                <PurchaseCard key={purchase.id} purchase={purchase} />
              ))}
            </div>
          )}

          {result.ok && purchases.length === 0 && (
            <div className="flex flex-col items-center justify-center py-16">
              <h2 className="text-lg font-semibold text-foreground mb-2">No purchases yet</h2>
              <p className="text-sm text-muted-foreground mb-6 text-center max-w-md">
                When you buy an asset, it will appear here. Browse the catalog to get started.
              </p>
              <Button
                asChild
                className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium"
              >
                <Link href="/assets">Browse assets</Link>
              </Button>
            </div>
          )}
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
