"use client";

import { Star, CheckCircle2 } from "lucide-react";
import type { AssetReview } from "@/lib/catalog-utils";

interface AssetReviewsListProps {
  reviews: AssetReview[];
}

export function AssetReviewsList({ reviews }: AssetReviewsListProps) {
  const dateFormatter = new Intl.DateTimeFormat("en-US", {
    year: "numeric",
    month: "short",
    day: "numeric",
  });

  return (
    <div className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold text-foreground">
        Reviews ({reviews.length})
      </h2>

      {reviews.length === 0 && (
        <div className="py-8 text-center">
          <p className="text-sm font-medium text-muted-foreground">No reviews yet.</p>
          <p className="text-xs text-muted-foreground/60 mt-1">Be the first to review after purchase.</p>
        </div>
      )}

      <div className="space-y-4">
        {reviews.map((review) => (
          <article
            key={review.id}
            className="rounded-lg border border-border bg-card-elevated p-4 flex flex-col gap-2"
          >
            {/* Header: name, stars, verified */}
            <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex items-center gap-2">
                <span className="text-sm font-medium text-foreground">
                  @{review.authorUsername}
                </span>
                {review.verifiedPurchase && (
                  <div className="flex items-center gap-1 text-xs text-accent">
                    <CheckCircle2 className="w-3.5 h-3.5" />
                    <span>Verified</span>
                  </div>
                )}
              </div>
              <div className="flex items-center gap-2">
                {/* Stars */}
                <div className="flex gap-0.5">
                  {Array(review.rating)
                    .fill(null)
                    .map((_, i) => (
                      <Star
                        key={`full-${i}`}
                        className="w-3.5 h-3.5 fill-accent text-accent"
                      />
                    ))}
                  {Array(5 - review.rating)
                    .fill(null)
                    .map((_, i) => (
                      <Star
                        key={`empty-${i}`}
                        className="w-3.5 h-3.5 text-muted-foreground/30"
                      />
                    ))}
                </div>
                <span className="text-xs text-muted-foreground">
                  {dateFormatter.format(new Date(review.createdAt))}
                </span>
              </div>
            </div>

            {/* Review body */}
            <p className="text-sm text-foreground leading-relaxed">
              {review.body}
            </p>
          </article>
        ))}
      </div>
    </div>
  );
}
