"use client";

import { SiteMain } from "@/components/layout/site-main";
import { SitePageContainer } from "@/components/layout/site-page-container";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { SellOverview } from "@/components/sell/sell-overview";
import { SellMyListings } from "@/components/sell/sell-my-listings";
import { AssetUploadForm } from "@/components/sell/asset-upload-form";

export function SellDashboard() {
  return (
    <SiteMain>
      <SitePageContainer variant="document" padding="document">
        <p className="text-xs font-mono text-accent tracking-widest uppercase mb-3">For creators</p>
        <h1 className="text-3xl sm:text-4xl font-semibold text-balance mb-8">Sell on AssetBlock</h1>

        <Tabs defaultValue="overview" className="gap-6">
          <TabsList className="bg-muted/80 border border-border/50 p-1 h-auto flex-wrap justify-start">
            <TabsTrigger value="overview" className="text-xs sm:text-sm">
              Overview
            </TabsTrigger>
            <TabsTrigger value="listings" className="text-xs sm:text-sm">
              My listings
            </TabsTrigger>
            <TabsTrigger value="upload" className="text-xs sm:text-sm">
              Upload asset
            </TabsTrigger>
          </TabsList>

          <TabsContent value="overview" className="mt-0 outline-none">
            <SellOverview />
          </TabsContent>

          <TabsContent value="listings" className="mt-0 outline-none">
            <SellMyListings />
          </TabsContent>

          <TabsContent value="upload" className="mt-0 outline-none">
            <AssetUploadForm />
          </TabsContent>
        </Tabs>
      </SitePageContainer>
    </SiteMain>
  );
}
