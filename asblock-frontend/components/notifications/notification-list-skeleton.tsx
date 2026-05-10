"use client";

import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

export function NotificationListSkeletonRow({ className }: { className?: string }) {
  return (
    <li className={cn("px-3 py-2.5", className)}>
      <div className="flex items-start justify-between gap-2">
        <Skeleton className="h-3.5 w-[45%] rounded-sm bg-muted-foreground/20 animate-pulse" />
        <Skeleton className="h-2.5 w-12 shrink-0 rounded-sm bg-muted-foreground/20 animate-pulse" />
      </div>
      <Skeleton className="mt-0.5 h-3 w-[88%] rounded-sm bg-muted-foreground/20 animate-pulse" />
      <Skeleton className="mt-1 h-2.5 w-16 rounded-sm bg-muted-foreground/20 animate-pulse" />
    </li>
  );
}

export interface NotificationListSkeletonProps {
  rows?: number;
  className?: string;
}

export function NotificationListSkeleton({ rows = 4, className }: NotificationListSkeletonProps) {
  return (
    <ul
      className={cn("divide-y divide-border", className)}
      aria-busy="true"
      aria-label="Loading notifications"
    >
      {Array.from({ length: rows }, (_, i) => (
        <NotificationListSkeletonRow key={i} />
      ))}
    </ul>
  );
}
