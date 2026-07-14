"use client";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { ChevronDown, Minus, Plus } from "lucide-react";
import { useState, useEffect, useCallback } from "react";
import {
  CATALOG_SORT_OPTIONS,
  getCatalogSortLabel,
  type CatalogFilters,
} from "@/lib/catalog/catalog-filters";
import { usePointerStepRepeat } from "@/hooks/use-pointer-step-repeat";

interface CatalogFiltersProps {
  filters: CatalogFilters;
  onFilterChange: (updates: Partial<CatalogFilters>) => void;
  onReset: () => void;
  categories: Array<{ id: string; name: string }>;
  tags: string[];
  facetsLoading?: boolean;
  isLoading?: boolean;
}

const FILTER_INPUT_DEBOUNCE_MS = 300;

function clampPricePair(
  min: number | null,
  max: number | null,
): { min: number | null; max: number | null } {
  let m = min;
  const x = max === 0 ? null : max;
  if (m !== null && x !== null && m > x) {
    m = Math.max(0, x - 1);
  }
  return { min: m, max: x };
}

export function CatalogFiltersUI({
  filters,
  onFilterChange,
  onReset,
  categories,
  tags,
  facetsLoading = false,
  isLoading = false,
}: CatalogFiltersProps) {
  const [searchInput, setSearchInput] = useState(filters.search);
  const [draftMin, setDraftMin] = useState<number | null>(filters.minPrice);
  const [draftMax, setDraftMax] = useState<number | null>(filters.maxPrice);

  useEffect(() => {
    queueMicrotask(() => {
      setSearchInput(filters.search);
    });
  }, [filters.search]);

  useEffect(() => {
    const timer = setTimeout(() => {
      if (searchInput !== filters.search) {
        onFilterChange({ search: searchInput, page: 1 });
      }
    }, FILTER_INPUT_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [searchInput, filters.search, onFilterChange]);

  useEffect(() => {
    queueMicrotask(() => {
      setDraftMin(filters.minPrice);
      setDraftMax(filters.maxPrice);
    });
  }, [filters.minPrice, filters.maxPrice]);

  useEffect(() => {
    const timer = setTimeout(() => {
      const clamped = clampPricePair(draftMin, draftMax);
      if (clamped.min !== draftMin || clamped.max !== draftMax) {
        setDraftMin(clamped.min);
        setDraftMax(clamped.max);
      }
      if (
        clamped.min === filters.minPrice &&
        clamped.max === filters.maxPrice
      ) {
        return;
      }
      onFilterChange({
        minPrice: clamped.min,
        maxPrice: clamped.max,
        page: 1,
      });
    }, FILTER_INPUT_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [draftMin, draftMax, filters.minPrice, filters.maxPrice, onFilterChange]);

  const handleTagToggle = (tag: string) => {
    const newTags = filters.tags.includes(tag)
      ? filters.tags.filter((t) => t !== tag)
      : [...filters.tags, tag];
    onFilterChange({ tags: newTags, page: 1 });
  };

  const applyDraftPrices = useCallback((min: number | null, max: number | null) => {
    const c = clampPricePair(min, max);
    setDraftMin(c.min);
    setDraftMax(c.max);
  }, []);

  const adjustPrice = useCallback(
    (field: "min" | "max", delta: number) => {
      if (field === "min") {
        const next = Math.max(0, (draftMin ?? 0) + delta);
        applyDraftPrices(next, draftMax);
      } else {
        const next = Math.max(0, (draftMax ?? 0) + delta);
        applyDraftPrices(draftMin, next);
      }
    },
    [draftMin, draftMax, applyDraftPrices],
  );

  const minMinusHold = usePointerStepRepeat(
    useCallback(() => adjustPrice("min", -1), [adjustPrice]),
  );
  const minPlusHold = usePointerStepRepeat(
    useCallback(() => adjustPrice("min", 1), [adjustPrice]),
  );
  const maxMinusHold = usePointerStepRepeat(
    useCallback(() => adjustPrice("max", -1), [adjustPrice]),
  );
  const maxPlusHold = usePointerStepRepeat(
    useCallback(() => adjustPrice("max", 1), [adjustPrice]),
  );

  const categoryLabel = filters.categoryId
    ? categories.find((c) => c.id === filters.categoryId)?.name ?? "Category"
    : "All categories";

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="search-assets" className="text-xs font-medium">
          Search
        </Label>
        <Input
          id="search-assets"
          type="text"
          placeholder="Search assets..."
          value={searchInput}
          onChange={(e) => setSearchInput(e.target.value)}
          className="bg-input border-border text-xs placeholder:text-muted-foreground/50 focus-visible:ring-primary h-8"
        />
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="category-menu-trigger" className="text-xs font-medium">
          Category
        </Label>
        {facetsLoading ? (
          <Skeleton
            className="h-8 w-full rounded-md bg-muted-foreground/20 animate-pulse"
            aria-busy="true"
            aria-label="Loading categories"
          />
        ) : (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                id="category-menu-trigger"
                type="button"
                variant="outline"
                className="bg-input border-border text-xs h-8 w-full justify-between font-normal px-3"
              >
                <span className="truncate">{categoryLabel}</span>
                <ChevronDown className="size-4 opacity-50 shrink-0" aria-hidden />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent
              align="start"
              className="w-[var(--radix-dropdown-menu-trigger-width)] max-h-64 overflow-y-auto z-[100]"
            >
              <DropdownMenuItem onSelect={() => onFilterChange({ categoryId: "", page: 1 })}>
                All categories
              </DropdownMenuItem>
              {categories.map((cat) => (
                <DropdownMenuItem
                  key={cat.id}
                  onSelect={() => onFilterChange({ categoryId: cat.id, page: 1 })}
                >
                  {cat.name}
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <Label className="text-xs font-medium">Tags</Label>
        {facetsLoading ? (
          <div
            className="space-y-1.5 max-h-48 overflow-hidden"
            aria-busy="true"
            aria-label="Loading tags"
          >
            {Array.from({ length: 7 }, (_, i) => (
              <div key={i} className="flex items-center gap-2">
                <Skeleton className="size-4 shrink-0 rounded-sm bg-muted-foreground/20 animate-pulse" />
                <Skeleton className="h-3 flex-1 max-w-[85%] rounded-sm bg-muted-foreground/20 animate-pulse" />
              </div>
            ))}
          </div>
        ) : (
          <div className="space-y-1.5 max-h-48 overflow-y-auto scrollbar-themed">
            {tags.map((tag) => (
              <div key={tag} className="flex items-center gap-2">
                <Checkbox
                  id={`tag-${tag}`}
                  checked={filters.tags.includes(tag)}
                  onCheckedChange={() => handleTagToggle(tag)}
                  className="border-border bg-input"
                />
                <Label
                  htmlFor={`tag-${tag}`}
                  className="text-xs cursor-pointer font-normal"
                >
                  {tag}
                </Label>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <span className="text-xs font-medium">Price range</span>
        <div className="flex gap-2">
          <div className="flex min-w-0 flex-1 flex-col gap-1">
            <Label htmlFor="price-min" className="text-[10px] font-normal text-muted-foreground">
              Min
            </Label>
            <div className="relative">
              <Input
                id="price-min"
                type="text"
                inputMode="numeric"
                autoComplete="off"
                pattern="[0-9]*"
                placeholder="0"
                value={draftMin ?? ""}
                onChange={(e) => {
                  const raw = e.target.value.replace(/\D/g, "");
                  applyDraftPrices(
                    raw === "" ? null : Number.parseInt(raw, 10),
                    draftMax,
                  );
                }}
                className="h-8 bg-input border-border pr-[4.25rem] pl-2 text-xs tabular-nums placeholder:text-muted-foreground/50"
              />
              <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-1">
                <div className="pointer-events-auto flex gap-0.5">
                  <button
                    type="button"
                    onPointerDown={(e) =>
                      minMinusHold.onPointerDown(!draftMin || draftMin <= 0, e)
                    }
                    onPointerUp={minMinusHold.onPointerEnd}
                    onPointerCancel={minMinusHold.onPointerEnd}
                    onLostPointerCapture={minMinusHold.onPointerEnd}
                    onKeyDown={(e) => {
                      if (e.key !== "Enter" && e.key !== " ") return;
                      e.preventDefault();
                      if (!draftMin || draftMin <= 0) return;
                      adjustPrice("min", -1);
                    }}
                    disabled={!draftMin || draftMin <= 0}
                    className="select-none rounded p-1 text-muted-foreground transition-colors hover:bg-secondary/80 hover:text-foreground disabled:cursor-default disabled:text-muted-foreground/30"
                    aria-label="Decrease min price"
                  >
                    <Minus className="h-3 w-3" />
                  </button>
                  <button
                    type="button"
                    onPointerDown={(e) => minPlusHold.onPointerDown(false, e)}
                    onPointerUp={minPlusHold.onPointerEnd}
                    onPointerCancel={minPlusHold.onPointerEnd}
                    onLostPointerCapture={minPlusHold.onPointerEnd}
                    onKeyDown={(e) => {
                      if (e.key !== "Enter" && e.key !== " ") return;
                      e.preventDefault();
                      adjustPrice("min", 1);
                    }}
                    className="select-none rounded p-1 text-muted-foreground transition-colors hover:bg-secondary/80 hover:text-foreground"
                    aria-label="Increase min price"
                  >
                    <Plus className="h-3 w-3" />
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div className="flex min-w-0 flex-1 flex-col gap-1">
            <Label htmlFor="price-max" className="text-[10px] font-normal text-muted-foreground">
              Max
            </Label>
            <div className="relative">
              <Input
                id="price-max"
                type="text"
                inputMode="numeric"
                autoComplete="off"
                pattern="[0-9]*"
                placeholder=""
                value={draftMax ?? ""}
                onChange={(e) => {
                  const raw = e.target.value.replace(/\D/g, "");
                  applyDraftPrices(
                    draftMin,
                    raw === "" ? null : Number.parseInt(raw, 10),
                  );
                }}
                className="h-8 bg-input border-border pr-[4.25rem] pl-2 text-xs tabular-nums placeholder:text-muted-foreground/50"
              />
              <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-1">
                <div className="pointer-events-auto flex gap-0.5">
                  <button
                    type="button"
                    onPointerDown={(e) =>
                      maxMinusHold.onPointerDown(!draftMax || draftMax <= 0, e)
                    }
                    onPointerUp={maxMinusHold.onPointerEnd}
                    onPointerCancel={maxMinusHold.onPointerEnd}
                    onLostPointerCapture={maxMinusHold.onPointerEnd}
                    onKeyDown={(e) => {
                      if (e.key !== "Enter" && e.key !== " ") return;
                      e.preventDefault();
                      if (!draftMax || draftMax <= 0) return;
                      adjustPrice("max", -1);
                    }}
                    disabled={!draftMax || draftMax <= 0}
                    className="select-none rounded p-1 text-muted-foreground transition-colors hover:bg-secondary/80 hover:text-foreground disabled:cursor-default disabled:text-muted-foreground/30"
                    aria-label="Decrease max price"
                  >
                    <Minus className="h-3 w-3" />
                  </button>
                  <button
                    type="button"
                    onPointerDown={(e) => maxPlusHold.onPointerDown(false, e)}
                    onPointerUp={maxPlusHold.onPointerEnd}
                    onPointerCancel={maxPlusHold.onPointerEnd}
                    onLostPointerCapture={maxPlusHold.onPointerEnd}
                    onKeyDown={(e) => {
                      if (e.key !== "Enter" && e.key !== " ") return;
                      e.preventDefault();
                      adjustPrice("max", 1);
                    }}
                    className="select-none rounded p-1 text-muted-foreground transition-colors hover:bg-secondary/80 hover:text-foreground"
                    aria-label="Increase max price"
                  >
                    <Plus className="h-3 w-3" />
                  </button>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="sort-menu-trigger" className="text-xs font-medium">
          Sort by
        </Label>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              id="sort-menu-trigger"
              type="button"
              variant="outline"
              className="bg-input border-border text-xs h-8 w-full justify-between font-normal px-3"
            >
              <span className="truncate">{getCatalogSortLabel(filters.sortBy)}</span>
              <ChevronDown className="size-4 opacity-50 shrink-0" aria-hidden />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent
            align="start"
            className="w-[var(--radix-dropdown-menu-trigger-width)] z-[100]"
          >
            {CATALOG_SORT_OPTIONS.map((opt) => (
              <DropdownMenuItem
                key={opt.value}
                onSelect={() => onFilterChange({ sortBy: opt.value })}
              >
                {opt.label}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>

      <div className="flex gap-2 pt-2">
        <Button
          type="button"
          onClick={onReset}
          variant="outline"
          size="sm"
          disabled={isLoading || facetsLoading}
          className="flex-1 border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground text-xs h-8"
        >
          Reset
        </Button>
      </div>
    </div>
  );
}
