import Link from "next/link";
import { SiteMain } from "@/components/layout/site-main";
import { SitePageContainer } from "@/components/layout/site-page-container";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";

export default function DocsPage() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="document" padding="document">
          <h1 className="text-3xl sm:text-4xl font-semibold text-balance mb-4">
            Documentation
          </h1>
          <p className="text-muted-foreground leading-relaxed mb-10">
            Placeholder docs for AssetBlock. Full guides and API reference will live here; for now
            use the interactive Swagger UI on your running API instance.
          </p>

          <section className="space-y-4 mb-12" id="getting-started">
            <h2 className="text-lg font-semibold text-foreground">Getting started</h2>
            <ul className="list-disc list-inside text-sm text-muted-foreground space-y-2">
              <li>
                <Link href="/register" className="text-accent hover:underline">
                  Create an account
                </Link>{" "}
                to buy or sell.
              </li>
              <li>
                <Link href="/assets" className="text-accent hover:underline">
                  Browse assets
                </Link>{" "}
                in the catalog.
              </li>
              <li>
                <Link href="/sell" className="text-accent hover:underline">
                  Learn about selling
                </Link>{" "}
                on the platform.
              </li>
            </ul>
          </section>

          <section className="space-y-4" id="api">
            <h2 className="text-lg font-semibold text-foreground">API reference</h2>
            <p className="text-sm text-muted-foreground leading-relaxed">
              The backend exposes OpenAPI / Swagger in Development. Run the WebApi project and open{" "}
              <span className="font-mono text-foreground/90">/swagger</span> on your API host
              (e.g. <span className="font-mono text-foreground/90">https://localhost:7000/swagger</span>
              ).
            </p>
          </section>
        </SitePageContainer>
      </SiteMain>
      <SiteFooter />
    </div>
  );
}
