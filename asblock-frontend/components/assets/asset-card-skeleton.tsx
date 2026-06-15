"use client";

import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

export type AssetCardSkeletonVariant = "catalog" | "featured";

interface AssetCardSkeletonProps {
  variant?: AssetCardSkeletonVariant;
  className?: string;
}

export function AssetCardSkeleton({ variant = "catalog", className }: AssetCardSkeletonProps) {
  const isFeatured = variant === "featured";
  return (
    <div
      className={cn(
        "flex-none rounded-xl border border-border flex flex-col gap-3",
        isFeatured
          ? "min-h-[19rem] h-full w-72 p-5 sm:w-80"
          : "w-full p-4",
        className,
      )}
      style={{ background: "#11101A" }}
      aria-hidden
    >
      <div className="flex items-start justify-between gap-2 h-12">
        <div className="flex flex-col gap-1.5 min-w-0 flex-1 justify-center">
          <Skeleton className="h-4 w-16 rounded-sm bg-muted-foreground/20 animate-pulse" />
          <Skeleton className="h-3.5 w-[88%] rounded-sm bg-muted-foreground/20 animate-pulse" />
        </div>
        <Skeleton className="h-5 w-14 shrink-0 rounded-sm bg-muted-foreground/20 animate-pulse" />
      </div>
      <div className="flex flex-col gap-1.5 flex-1 min-h-[2.5rem]">
        <Skeleton className="h-3 w-full rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-3 w-[92%] rounded-sm bg-muted-foreground/20 animate-pulse" />
      </div>
      <div className="flex flex-wrap gap-1.5 h-7">
        <Skeleton className="h-5 w-12 rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-5 w-14 rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-5 w-10 rounded-sm bg-muted-foreground/20 animate-pulse" />
      </div>
      <div className="border-t border-border pt-3 flex flex-col gap-3 mt-auto">
        <div className="flex items-center justify-between">
          <Skeleton className="h-3 w-24 rounded-sm bg-muted-foreground/20 animate-pulse" />
          <Skeleton className="h-3 w-16 rounded-sm bg-muted-foreground/20 animate-pulse" />
        </div>
        <Skeleton className="h-9 w-full rounded-lg bg-muted-foreground/20 animate-pulse" />
      </div>
    </div>
  );
}

interface AssetCardGridSkeletonProps {
  count?: number;
  /** Desktop catalog: 3 cols on lg */
  variant?: "catalog-desktop" | "catalog-mobile";
  className?: string;
}

export function AssetCardGridSkeleton({
  count,
  variant = "catalog-desktop",
  className,
}: AssetCardGridSkeletonProps) {
  const defaultCount = variant === "catalog-mobile" ? 4 : 6;
  const n = count ?? defaultCount;
  const grid =
    variant === "catalog-mobile"
      ? "grid grid-cols-1 sm:grid-cols-2 gap-4"
      : "grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4";

  return (
    <div
      className={cn(grid, className)}
      aria-busy="true"
      aria-label="Loading assets"
    >
      {Array.from({ length: n }, (_, i) => (
        <AssetCardSkeleton key={i} variant="catalog" />
      ))}
    </div>
  );
}

export function FeaturedAssetCarouselSkeleton({ count = 4 }: { count?: number }) {
  return (
    <div
      className="flex gap-4 items-stretch overflow-hidden pb-2 -mx-4 px-4 sm:-mx-6 sm:px-6 lg:mx-0 lg:px-0"
      aria-busy="true"
      aria-label="Loading featured assets"
    >
      {Array.from({ length: count }, (_, i) => (
        <AssetCardSkeleton key={i} variant="featured" />
      ))}
    </div>
  );
}
