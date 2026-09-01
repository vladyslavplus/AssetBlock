'use client'

import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import Link from 'next/link'
import { ChevronLeft, ChevronRight, ArrowRight } from 'lucide-react'
import type { AssetListItem } from '@/lib/catalog/asset-types'
import { catalogKeys, fetchFeaturedAssets } from '@/lib/catalog/catalog-query'
import { AssetCard } from '@/components/assets/asset-card'
import { FeaturedAssetCarouselSkeleton } from '@/components/assets/asset-card-skeleton'
import { Button } from '@/components/ui/button'
import { siteShellClass } from '@/lib/site-layout'
import { runQueryInBackground } from '@/lib/query/query-refresh'

const EMPTY_ASSETS: AssetListItem[] = []

function updateScrollAvailability(
  element: HTMLDivElement | null,
  setCanScrollLeft: (value: boolean) => void,
  setCanScrollRight: (value: boolean) => void,
) {
  if (!element) {
    setCanScrollLeft(false)
    setCanScrollRight(false)
    return
  }

  const maxScroll = element.scrollWidth - element.clientWidth
  if (maxScroll <= 1) {
    setCanScrollLeft(false)
    setCanScrollRight(false)
    return
  }

  setCanScrollLeft(element.scrollLeft > 4)
  setCanScrollRight(element.scrollLeft < maxScroll - 4)
}

const FEATURED_LIMIT = 8

export function FeaturedAssetsSection() {
  const scrollRef = useRef<HTMLDivElement>(null)
  const [canScrollLeft, setCanScrollLeft] = useState(false)
  const [canScrollRight, setCanScrollRight] = useState(false)

  const featuredQuery = useQuery({
    queryKey: catalogKeys.featured(FEATURED_LIMIT),
    // Do not forward TanStack Query's cancellation signal to browser fetch. On navigation,
    // Next dev reports the expected abort as an unhandled rejection even though the query owns it.
    queryFn: () => fetchFeaturedAssets({ limit: FEATURED_LIMIT }),
  })

  const assets = featuredQuery.data ?? EMPTY_ASSETS
  const loading = featuredQuery.isPending
  const loadError = featuredQuery.isError

  const SCROLL_AMOUNT = 340

  // Defer scroll metrics until after layout (avoids sync setState in layout effect).
  useEffect(() => {
    if (loading || assets.length === 0) {
      return
    }
    const id = window.requestAnimationFrame(() =>
      updateScrollAvailability(scrollRef.current, setCanScrollLeft, setCanScrollRight),
    )
    return () => window.cancelAnimationFrame(id)
  }, [assets, loading])

  useEffect(() => {
    const el = scrollRef.current
    if (!el) {
      return
    }
    const update = () =>
      updateScrollAvailability(scrollRef.current, setCanScrollLeft, setCanScrollRight)
    const ro = new ResizeObserver(update)
    ro.observe(el)
    window.addEventListener('resize', update)
    return () => {
      ro.disconnect()
      window.removeEventListener('resize', update)
    }
  }, [assets, loading])

  const scrollLeft = () => {
    scrollRef.current?.scrollBy({ left: -SCROLL_AMOUNT, behavior: 'smooth' })
  }

  const scrollRight = () => {
    scrollRef.current?.scrollBy({ left: SCROLL_AMOUNT, behavior: 'smooth' })
  }

  const showCarousel = !loading && !loadError && assets.length > 0
  const showEmpty = !loading && !loadError && assets.length === 0

  return (
    <section className="py-20 sm:py-28" aria-labelledby="featured-heading">
      <div className={siteShellClass('site')}>
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

          {showCarousel && (
            <div className="mt-6 flex flex-col items-center gap-3 sm:mt-0 sm:absolute sm:right-0 sm:top-0 sm:items-end">
              <div className="flex items-center gap-2 shrink-0">
                <button
                  type="button"
                  onClick={scrollLeft}
                  disabled={!canScrollLeft}
                  aria-label="Scroll left"
                  className={`w-9 h-9 rounded-lg border flex items-center justify-center transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background ${
                    canScrollLeft
                      ? 'border-border text-foreground hover:bg-secondary/50 hover:border-foreground/40 active:bg-muted cursor-pointer'
                      : 'border-border/30 text-muted-foreground/30 cursor-not-allowed'
                  }`}
                >
                  <ChevronLeft className="w-4 h-4" />
                </button>
                <button
                  type="button"
                  onClick={scrollRight}
                  disabled={!canScrollRight}
                  aria-label="Scroll right"
                  className={`w-9 h-9 rounded-lg border flex items-center justify-center transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background ${
                    canScrollRight
                      ? 'border-border text-foreground hover:bg-secondary/50 hover:border-foreground/40 active:bg-muted cursor-pointer'
                      : 'border-border/30 text-muted-foreground/30 cursor-not-allowed'
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
          )}
        </div>

        {loading && (
          <div className="py-4">
            <FeaturedAssetCarouselSkeleton count={4} />
          </div>
        )}

        {loadError && (
          <div className="rounded-xl border border-border bg-card-elevated/50 px-6 py-12 text-center">
            <p className="text-foreground font-medium mb-1">Couldn&apos;t load featured assets</p>
            <p className="text-sm text-muted-foreground mb-4">
              Check that the API is running and{' '}
              <span className="font-mono">NEXT_PUBLIC_API_BASE_URL</span> is set.
            </p>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => runQueryInBackground(featuredQuery.refetch({ cancelRefetch: false }))}
            >
              Try again
            </Button>
          </div>
        )}

        {showEmpty && (
          <div className="rounded-xl border border-dashed border-border bg-card-elevated/30 px-6 py-14 text-center">
            <p className="text-foreground font-medium mb-2">No assets in the catalog yet</p>
            <p className="text-sm text-muted-foreground mb-6 max-w-md mx-auto">
              Once sellers publish products, they&apos;ll show up here automatically.
            </p>
            <div className="flex flex-wrap justify-center gap-3">
              <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
                <Link href="/sell">Start selling</Link>
              </Button>
              <Button variant="outline" asChild className="border-border bg-transparent">
                <Link href="/assets">Browse catalog</Link>
              </Button>
            </div>
          </div>
        )}

        {showCarousel && (
          <div
            ref={scrollRef}
            onScroll={() =>
              updateScrollAvailability(scrollRef.current, setCanScrollLeft, setCanScrollRight)
            }
            className="flex gap-4 items-stretch overflow-x-auto scrollbar-hide pb-2 -mx-4 px-4 sm:-mx-6 sm:px-6 lg:mx-0 lg:px-0"
            style={{ scrollbarWidth: 'none' }}
            role="list"
            aria-label="Featured assets carousel"
          >
            {assets.map((asset) => (
              <div key={asset.id} role="listitem" className="flex h-full">
                <AssetCard asset={asset} variant="carousel" linkSource="catalog" />
              </div>
            ))}
          </div>
        )}

        <div className="mt-8 flex justify-center">
          <Link
            href="/sell"
            className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground transition-smooth group"
          >
            <span>Want to start selling?</span>
            <ArrowRight className="w-4 h-4 group-hover:translate-x-1 transition-transform" />
          </Link>
        </div>
      </div>
    </section>
  )
}
