import { SiteHeader } from "@/components/site-header";
import { HeroSection } from "@/components/hero-section";
import { FeaturesSection } from "@/components/features-section";
import { FeaturedAssetsSection } from "@/components/featured-assets-section";
import { HowItWorksSection } from "@/components/how-it-works-section";
import { FinalCtaSection } from "@/components/final-cta-section";
import { SiteFooter } from "@/components/site-footer";

export default function HomePage() {
  return (
    <div className="min-h-screen bg-background text-foreground">
      <SiteHeader />
      <main>
        <HeroSection />
        <FeaturesSection />
        <FeaturedAssetsSection />
        <HowItWorksSection />
        <FinalCtaSection />
      </main>
      <SiteFooter />
    </div>
  );
}
