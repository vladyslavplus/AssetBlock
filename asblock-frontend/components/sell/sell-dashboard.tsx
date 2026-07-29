'use client'

import dynamic from 'next/dynamic'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import { Suspense, useEffect } from 'react'

import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SellOverview } from '@/components/sell/sell-overview'
import { SellMyListings } from '@/components/sell/sell-my-listings'
import { SellMyCollections } from '@/components/sell/sell-my-collections'
import { SellMyBundles } from '@/components/sell/sell-my-bundles'
import { AssetUploadForm } from '@/components/sell/asset-upload-form'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import { isValidSellTab, parseSellTab, type SellTab } from '@/lib/sell/sell-tabs'

const SellAnalyticsDashboard = dynamic(
  () =>
    import('@/components/sell/analytics/sell-analytics-dashboard').then(
      (mod) => mod.SellAnalyticsDashboard,
    ),
  {
    loading: () => <SessionBlockSkeleton />,
    ssr: false,
  },
)

function SellDashboardInner() {
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()

  const tab = parseSellTab(searchParams.get('tab'))

  useEffect(() => {
    const current = new URLSearchParams(searchParams.toString())
    const rawTab = current.get('tab')
    if (rawTab && !isValidSellTab(rawTab)) {
      const patched = new URLSearchParams(current.toString())
      patched.delete('tab')
      const qs = patched.toString()
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    }
  }, [pathname, router, searchParams])

  function setTab(nextTab: SellTab) {
    const params = new URLSearchParams(searchParams.toString())
    if (nextTab === 'overview') {
      params.delete('tab')
    } else {
      params.set('tab', nextTab)
    }
    const qs = params.toString()
    router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
  }

  return (
    <SiteMain>
      <SitePageContainer variant="document" padding="document">
        <p className="text-xs font-mono text-accent tracking-widest uppercase mb-3">For creators</p>
        <h1 className="text-3xl sm:text-4xl font-semibold text-balance mb-8">Sell on AssetBlock</h1>

        <Tabs value={tab} onValueChange={(value) => setTab(value as SellTab)} className="gap-6">
          <TabsList className="bg-muted/80 border border-border/50 p-1 h-auto flex-wrap justify-start">
            <TabsTrigger value="overview" className="text-xs sm:text-sm">
              Overview
            </TabsTrigger>
            <TabsTrigger value="analytics" className="text-xs sm:text-sm">
              Analytics
            </TabsTrigger>
            <TabsTrigger value="listings" className="text-xs sm:text-sm">
              My listings
            </TabsTrigger>
            <TabsTrigger value="collections" className="text-xs sm:text-sm">
              Collections
            </TabsTrigger>
            <TabsTrigger value="bundles" className="text-xs sm:text-sm">
              Bundles
            </TabsTrigger>
            <TabsTrigger value="upload" className="text-xs sm:text-sm">
              Upload asset
            </TabsTrigger>
          </TabsList>

          <TabsContent value="overview" className="mt-0 outline-none">
            <SellOverview />
          </TabsContent>

          <TabsContent value="analytics" className="mt-0 outline-none">
            <SellAnalyticsDashboard />
          </TabsContent>

          <TabsContent value="listings" className="mt-0 outline-none">
            <SellMyListings />
          </TabsContent>

          <TabsContent value="collections" className="mt-0 outline-none">
            <SellMyCollections />
          </TabsContent>

          <TabsContent value="bundles" className="mt-0 outline-none">
            <SellMyBundles />
          </TabsContent>

          <TabsContent value="upload" className="mt-0 outline-none">
            <AssetUploadForm />
          </TabsContent>
        </Tabs>
      </SitePageContainer>
    </SiteMain>
  )
}

export function SellDashboard() {
  return (
    <Suspense fallback={<SessionBlockSkeleton />}>
      <SellDashboardInner />
    </Suspense>
  )
}
