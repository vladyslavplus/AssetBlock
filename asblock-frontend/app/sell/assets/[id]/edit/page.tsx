import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { ArrowLeft } from "lucide-react";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { AssetEditPageClient } from "@/components/sell/asset-edit-page-client";
import { getAssetDetailCached } from "@/lib/server/asset-detail-server";

interface PageProps {
  params: Promise<{ id: string }>;
}

export async function generateMetadata({ params }: PageProps): Promise<Metadata> {
  const { id } = await params;
  const raw = await getAssetDetailCached(id);
  if (!raw) {
    return { title: "Edit asset · AssetBlock" };
  }
  return {
    title: `Edit · ${raw.title} · AssetBlock`,
    description: "Update your marketplace listing.",
  };
}

export default async function SellAssetEditPage({ params }: PageProps) {
  const { id } = await params;
  const raw = await getAssetDetailCached(id);
  if (!raw) {
    notFound();
  }

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-12">
          <Link
            href="/sell"
            className="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors mb-6"
          >
            <ArrowLeft className="w-4 h-4" />
            Back to Sell
          </Link>

          <p className="text-xs font-mono text-accent tracking-widest uppercase mb-3">Seller</p>
          <h1 className="text-3xl sm:text-4xl font-semibold text-balance mb-8">Edit listing</h1>

          <AssetEditPageClient initialAsset={raw} />
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
