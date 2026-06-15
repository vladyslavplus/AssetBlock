"use client";

import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

export function SessionBlockSkeleton({
  className,
  lines = 2,
}: {
  className?: string;
  lines?: number;
}) {
  return (
    <div
      className={cn("flex flex-col gap-2 py-8", className)}
      aria-busy="true"
      aria-label="Loading"
    >
      {Array.from({ length: lines }, (_, i) => (
        <Skeleton
          key={i}
          className={cn(
            "h-3.5 rounded-sm bg-muted-foreground/20 animate-pulse",
            i === 0 ? "w-[min(100%,14rem)]" : "w-[min(100%,10rem)]",
          )}
        />
      ))}
    </div>
  );
}
