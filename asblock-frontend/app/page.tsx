import { SiteHeader } from '@/components/site-header'
import { HeroSection } from '@/components/hero-section'
import { FeaturesSection } from '@/components/features-section'
import { FeaturedAssetsSection } from '@/components/featured-assets-section'
import { HowItWorksSection } from '@/components/how-it-works-section'
import { FinalCtaSection } from '@/components/final-cta-section'
import { SiteFooter } from '@/components/site-footer'
import { DEFAULT_FEATURED_LIMIT } from '@/lib/catalog/catalog-query'
import { getFeaturedAssetsCached } from '@/lib/server/catalog-server'

export default async function HomePage() {
  const initialAssets = await getFeaturedAssetsCached(DEFAULT_FEATURED_LIMIT)

  return (
    <div className="min-h-screen bg-background text-foreground">
      <SiteHeader />
      <main id="main-content" tabIndex={-1} className="outline-none">
        <HeroSection />
        <FeaturesSection />
        <FeaturedAssetsSection initialAssets={initialAssets ?? undefined} />
        <HowItWorksSection />
        <FinalCtaSection />
      </main>
      <SiteFooter />
    </div>
  )
}
