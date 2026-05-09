import Link from "next/link";
import { notFound } from "next/navigation";
import { ArrowLeft } from "lucide-react";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { AssetDetailHero } from "@/components/assets/asset-detail-hero";
import { AssetReviewsList } from "@/components/assets/asset-reviews-list";
import { AssetPurchaseCard } from "@/components/assets/asset-purchase-card";
import { mapDetailApiToListItemForHero } from "@/lib/assets-api";
import { getAssetDetailCached, getAssetReviewsCached } from "@/lib/server/asset-detail-server";

interface AssetDetailPageProps {
  params: Promise<{ id: string }>;
}

export async function generateMetadata({ params }: AssetDetailPageProps) {
  const resolvedParams = await params;
  try {
    const raw = await getAssetDetailCached(resolvedParams.id);
    if (!raw) {
      return { title: "Asset not found · AssetBlock" };
    }
    const asset = mapDetailApiToListItemForHero(raw);
    return {
      title: `${asset.title} · AssetBlock`,
      description:
        asset.description?.trim() ||
        "Discover premium digital assets on AssetBlock marketplace.",
    };
  } catch {
    return { title: "AssetBlock" };
  }
}

export default async function AssetDetailPage({ params }: AssetDetailPageProps) {
  const resolvedParams = await params;
  const raw = await getAssetDetailCached(resolvedParams.id);
  if (!raw) {
    notFound();
  }

  const asset = mapDetailApiToListItemForHero(raw);
  const reviews = await getAssetReviewsCached(resolvedParams.id);

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <main className="flex-1 px-4 sm:px-6 lg:px-8 pt-20 pb-16">
        <div className="max-w-6xl mx-auto">
          <Link
            href="/assets"
            className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
          >
            <ArrowLeft className="w-4 h-4" />
            Back to catalog
          </Link>

          <div className="grid grid-cols-1 lg:grid-cols-3 gap-8">
            <div className="lg:col-span-2 flex flex-col gap-8">
              <AssetDetailHero asset={asset} />

              <div className="flex flex-col gap-2">
                <h2 className="text-lg font-semibold text-foreground">Description</h2>
                <p className="text-sm text-foreground leading-relaxed">
                  {asset.description?.trim() ? (
                    asset.description
                  ) : (
                    <span className="text-muted-foreground">No description provided yet.</span>
                  )}
                </p>
              </div>

              <AssetReviewsList reviews={reviews} />
            </div>

            <div className="lg:col-span-1">
              <div className="lg:sticky lg:top-24">
                <AssetPurchaseCard title={asset.title} price={asset.price} />
              </div>
            </div>
          </div>
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
