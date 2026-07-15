import type { Metadata } from 'next'
import { SiteHeader } from '@/components/site-header'
import { SiteFooter } from '@/components/site-footer'
import { SellDashboard } from '@/components/sell/sell-dashboard'

export const metadata: Metadata = {
  title: 'Sell · AssetBlock',
  description: 'List and upload digital assets on the AssetBlock marketplace.',
}

export default function SellPage() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SellDashboard />
      <SiteFooter />
    </div>
  )
}
