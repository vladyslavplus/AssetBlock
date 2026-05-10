import { LibraryGridSkeleton } from "@/components/library/library-purchase-card-skeleton";
import { SiteFooter } from "@/components/site-footer";
import { SiteHeader } from "@/components/site-header";
import { Skeleton } from "@/components/ui/skeleton";

export default function LibraryLoading() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="mb-8 space-y-2">
            <Skeleton className="h-9 w-48 rounded-md bg-muted-foreground/20 animate-pulse" />
            <Skeleton className="h-4 w-72 max-w-full rounded-md bg-muted-foreground/20 animate-pulse" />
          </div>
          <LibraryGridSkeleton />
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
