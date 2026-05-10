import { notFound } from "next/navigation";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { AssetDetailView } from "@/components/assets/asset-detail-view";
import { mapDetailApiToListItemForHero } from "@/lib/catalog/assets-api";
import { getAssetDetailCached, getAssetReviewsCached } from "@/lib/server/asset-detail-server";
import { fetchPaymentsCapabilitiesServer } from "@/lib/server/payments-capabilities";

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

  const reviews = await getAssetReviewsCached(resolvedParams.id);
  const { checkoutConfigured } = await fetchPaymentsCapabilitiesServer();

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <AssetDetailView
        assetId={resolvedParams.id}
        initialDetail={raw}
        initialReviews={reviews}
        checkoutConfigured={checkoutConfigured}
      />

      <SiteFooter />
    </div>
  );
}
