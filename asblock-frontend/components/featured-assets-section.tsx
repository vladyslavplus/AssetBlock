"use client";

import { useRef, useState, useCallback } from "react";
import Link from "next/link";
import { ChevronLeft, ChevronRight, ArrowRight } from "lucide-react";
import type { AssetListItem } from "@/lib/asset-types";
import { FEATURED_ASSETS_MOCK } from "@/lib/asset-types";

function StarRating({ value }: { value: number }) {
  const rounded = Math.round(value * 2) / 2;
  return (
    <div className="flex items-center gap-1" aria-label={`Rating: ${value.toFixed(1)} out of 5`}>
      <div className="flex">
        {[1, 2, 3, 4, 5].map((i) => {
          const full = i <= Math.floor(rounded);
          const half = !full && i === Math.ceil(rounded) && rounded % 1 !== 0;
          return (
            <svg
              key={i}
              className={`w-3.5 h-3.5 ${full || half ? "text-yellow-400" : "text-border"} fill-current`}
              viewBox="0 0 20 20"
              aria-hidden="true"
            >
              <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
            </svg>
          );
        })}
      </div>
      <span className="text-xs text-muted-foreground font-mono">{value.toFixed(1)}</span>
    </div>
  );
}

function AssetCard({ asset }: { asset: AssetListItem }) {
  const visibleTags = asset.tags.slice(0, 3);
  const overflowCount = asset.tags.length - visibleTags.length;

  return (
    <article
      className="flex-none w-72 sm:w-80 h-full min-h-[19rem] rounded-xl border border-border p-5 flex flex-col gap-4 group transition-smooth hover:border-primary/50 hover:bg-card-elevated hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)] focus-within:ring-2 focus-within:ring-primary focus-within:ring-offset-2 focus-within:ring-offset-background"
      style={{ background: "#11101A" }}
    >
      <div className="flex items-start justify-between gap-2 h-12">
        <div className="flex flex-col gap-1.5 min-w-0">
          {asset.categoryName && (
            <span className="text-[10px] font-mono text-muted-foreground uppercase tracking-wider border border-border px-2 py-0.5 rounded w-fit bg-secondary">
              {asset.categoryName}
            </span>
          )}
          <h3 className="font-semibold text-foreground text-sm leading-snug line-clamp-2 text-balance">
            {asset.title}
          </h3>
        </div>
        <span className="text-lg font-semibold text-foreground shrink-0 font-mono">
          ${asset.price}
        </span>
      </div>

      <div className="flex-1 min-h-[2.5rem] flex flex-col">
        {asset.description ? (
          <p className="text-xs text-muted-foreground leading-relaxed line-clamp-2">{asset.description}</p>
        ) : (
          <span className="text-xs text-muted-foreground/40" aria-hidden="true">
            &nbsp;
          </span>
        )}
      </div>

      {asset.tags.length > 0 && (
        <div className="flex flex-wrap gap-1.5 min-h-7 content-start">
          {visibleTags.map((tag) => (
            <span
              key={tag}
              className="px-2 py-0.5 rounded text-[10px] font-mono bg-secondary text-muted-foreground border border-border"
            >
              {tag}
            </span>
          ))}
          {overflowCount > 0 && (
            <span className="px-2 py-0.5 rounded text-[10px] font-mono bg-secondary text-muted-foreground border border-border">
              +{overflowCount}
            </span>
          )}
        </div>
      )}

      <div className="border-t border-border pt-3 flex flex-col gap-3 mt-auto">
        <div className="flex items-center justify-between">
          <span className="text-xs text-muted-foreground">
            <span className="text-accent">@{asset.authorUsername}</span>
          </span>
          <StarRating value={asset.averageRating} />
        </div>
        <button
          className="w-full px-3 py-2 rounded-lg border border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth text-xs font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card"
        >
          View details
        </button>
      </div>
    </article>
  );
}

export function FeaturedAssetsSection() {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [canScrollLeft, setCanScrollLeft] = useState(false);
  const [canScrollRight, setCanScrollRight] = useState(true);

  const SCROLL_AMOUNT = 340;

  const updateScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    setCanScrollLeft(el.scrollLeft > 4);
    setCanScrollRight(el.scrollLeft < el.scrollWidth - el.clientWidth - 4);
  }, []);

  const scrollLeft = () => {
    scrollRef.current?.scrollBy({ left: -SCROLL_AMOUNT, behavior: "smooth" });
    setTimeout(updateScrollState, 350);
  };

  const scrollRight = () => {
    scrollRef.current?.scrollBy({ left: SCROLL_AMOUNT, behavior: "smooth" });
    setTimeout(updateScrollState, 350);
  };

  return (
    <section className="py-20 sm:py-28" aria-labelledby="featured-heading">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="relative mb-8">
          <div className="text-center max-w-2xl mx-auto px-2 sm:px-16 md:px-20 lg:px-28">
            <h2
              id="featured-heading"
              className="text-3xl sm:text-4xl font-semibold text-foreground text-balance animate-fade-in"
            >
              Featured assets
            </h2>
            <p className="mt-2 text-muted-foreground text-base leading-relaxed animate-fade-in">
              Handpicked by the community this week.
            </p>
          </div>

          <div className="mt-6 flex flex-col items-center gap-3 sm:mt-0 sm:absolute sm:right-0 sm:top-0 sm:items-end">
            <div className="flex items-center gap-2 shrink-0">
              <button
                onClick={scrollLeft}
                disabled={!canScrollLeft}
                aria-label="Scroll left"
                className={`w-9 h-9 rounded-lg border flex items-center justify-center transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background
                ${
                  canScrollLeft
                    ? "border-border text-foreground hover:bg-secondary/50 hover:border-foreground/40 active:bg-muted cursor-pointer"
                    : "border-border/30 text-muted-foreground/30 cursor-default"
                }`}
              >
                <ChevronLeft className="w-4 h-4" />
              </button>
              <button
                onClick={scrollRight}
                disabled={!canScrollRight}
                aria-label="Scroll right"
                className={`w-9 h-9 rounded-lg border flex items-center justify-center transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background
                ${
                  canScrollRight
                    ? "border-border text-foreground hover:bg-secondary/50 hover:border-foreground/40 active:bg-muted cursor-pointer"
                    : "border-border/30 text-muted-foreground/30 cursor-default"
                }`}
              >
                <ChevronRight className="w-4 h-4" />
              </button>
            </div>
            <Link
              href="/assets"
              className="inline-flex items-center gap-1.5 text-sm font-medium text-foreground hover:text-accent transition-smooth group shrink-0"
            >
              Browse all
              <ArrowRight className="w-4 h-4 group-hover:translate-x-1 transition-transform" />
            </Link>
          </div>
        </div>

        <div
          ref={scrollRef}
          onScroll={updateScrollState}
          className="flex gap-4 items-stretch overflow-x-auto scrollbar-hide pb-2 -mx-4 px-4 sm:-mx-6 sm:px-6 lg:mx-0 lg:px-0"
          style={{ scrollbarWidth: "none" }}
          role="list"
          aria-label="Featured assets carousel"
        >
          {FEATURED_ASSETS_MOCK.map((asset) => (
            <div key={asset.id} role="listitem" className="flex h-full">
              <AssetCard asset={asset} />
            </div>
          ))}
        </div>

        <div className="mt-8 flex justify-center">
          <Link
            href="#"
            className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground transition-smooth group"
          >
            <span>Want to start selling?</span>
            <ArrowRight className="w-4 h-4 group-hover:translate-x-1 transition-transform" />
          </Link>
        </div>
      </div>
    </section>
  );
}
