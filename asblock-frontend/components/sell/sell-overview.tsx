import Link from 'next/link'
import { Button } from '@/components/ui/button'
import {
  ArrowRight,
  BarChart3,
  Store,
  Upload,
  ShieldCheck,
  FolderOpen,
  Package,
} from 'lucide-react'

export function SellOverview() {
  return (
    <div className="space-y-8">
      <p className="text-muted-foreground leading-relaxed">
        List templates, starter kits, CLI tools, and other digital products. Use{' '}
        <strong className="text-foreground font-medium">My listings</strong> for individual assets,{' '}
        <strong className="text-foreground font-medium">Collections</strong> for editorial
        groupings, and <strong className="text-foreground font-medium">Bundles</strong> for
        discounted multi-asset offers.
      </p>

      <div className="rounded-lg border border-border/60 bg-card/40 px-4 py-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-start gap-3">
            <BarChart3 className="mt-0.5 size-5 shrink-0 text-primary" aria-hidden />
            <div>
              <p className="font-medium text-foreground">Track sales in Analytics</p>
              <p className="mt-1 text-sm text-muted-foreground">
                View revenue, orders, product performance, and recent sales once checkout completes.
              </p>
            </div>
          </div>
          <Button asChild variant="outline" className="shrink-0 border-border bg-transparent">
            <Link href="/sell?tab=analytics">Open analytics</Link>
          </Button>
        </div>
      </div>

      <ul className="space-y-6">
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
          <FolderOpen className="w-5 h-5 text-primary shrink-0 mt-0.5" aria-hidden />
          <div>
            <p className="font-medium text-foreground">Curate collections</p>
            <p className="text-sm text-muted-foreground mt-1">
              Publish editorial collections that showcase related assets — no separate price.
            </p>
          </div>
        </li>
        <li className="flex gap-3">
          <Package className="w-5 h-5 text-primary shrink-0 mt-0.5" aria-hidden />
          <div>
            <p className="font-medium text-foreground">Sell bundles</p>
            <p className="text-sm text-muted-foreground mt-1">
              Offer 2–20 of your assets at one discounted price with a single checkout.
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
  )
}
