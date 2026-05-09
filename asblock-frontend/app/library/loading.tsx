import { SiteFooter } from "@/components/site-footer";
import { SiteHeader } from "@/components/site-header";

export default function LibraryLoading() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="mb-8 space-y-2">
            <div className="h-9 w-48 rounded-md bg-muted/40 animate-pulse" />
            <div className="h-4 w-72 rounded-md bg-muted/30 animate-pulse" />
          </div>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
            {Array.from({ length: 6 }).map((_, i) => (
              <div
                key={i}
                className="rounded-xl border border-border bg-card-elevated p-4 space-y-3 animate-pulse"
              >
                <div className="h-5 w-full rounded bg-muted/40" />
                <div className="h-3 w-2/3 rounded bg-muted/30" />
                <div className="h-3 w-full rounded bg-muted/25" />
                <div className="flex gap-2 pt-2">
                  <div className="h-8 flex-1 rounded-md bg-muted/35" />
                  <div className="h-8 flex-1 rounded-md bg-muted/35" />
                </div>
              </div>
            ))}
          </div>
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
