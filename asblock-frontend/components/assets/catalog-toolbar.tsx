"use client";

import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ChevronDown, Filter } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import {
  CATALOG_SORT_OPTIONS,
  getCatalogSortLabel,
  type CatalogFilters,
} from "@/lib/catalog/catalog-filters";

interface CatalogToolbarProps {
  filters: CatalogFilters;
  totalCount: number;
  displayCount: number;
  onFilterChange: (updates: Partial<CatalogFilters>) => void;
  onFilterClick?: () => void;
  isDesktop?: boolean;
  disabled?: boolean;
  isCountsLoading?: boolean;
}

export function CatalogToolbar({
  filters,
  totalCount,
  displayCount,
  onFilterChange,
  onFilterClick,
  isDesktop = false,
  disabled = false,
  isCountsLoading = false,
}: CatalogToolbarProps) {
  return (
    <div className="flex items-center justify-between gap-3 flex-wrap">
      {isCountsLoading ? (
        <Skeleton
          className="h-3.5 w-44 max-w-[70vw] rounded-sm bg-muted-foreground/20 animate-pulse"
          aria-busy="true"
          aria-label="Loading result count"
        />
      ) : (
        <p className="text-xs text-muted-foreground">
          Showing {displayCount} of {totalCount} assets
        </p>
      )}

      <div className="flex items-center gap-2">
        {!isDesktop && (
          <Button
            size="sm"
            variant="outline"
            onClick={onFilterClick}
            disabled={disabled}
            className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 text-xs h-8 gap-1.5"
          >
            <Filter className="w-3 h-3" />
            Filters
          </Button>
        )}

        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              type="button"
              variant="outline"
              disabled={disabled}
              className="bg-input border-border text-xs h-8 w-32 justify-between font-normal px-3"
            >
              <span className="truncate">{getCatalogSortLabel(filters.sortBy)}</span>
              <ChevronDown className="size-4 opacity-50 shrink-0" aria-hidden />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="min-w-[8rem] z-[100]">
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
    </div>
  );
}
