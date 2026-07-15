'use client'

import { Button } from '@/components/ui/button'
import { CatalogPriceFilter } from '@/components/assets/catalog-price-filter'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import { ChevronDown } from 'lucide-react'
import {
  CATALOG_SORT_OPTIONS,
  getCatalogSortLabel,
  type CatalogFilters,
} from '@/lib/catalog/catalog-filters'
import { useCatalogFilterState } from '@/lib/catalog/use-catalog-filter-state'

interface CatalogFiltersProps {
  filters: CatalogFilters
  onFilterChange: (updates: Partial<CatalogFilters>) => void
  onReset: () => void
  categories: Array<{ id: string; name: string }>
  tags: string[]
  facetsLoading?: boolean
  isLoading?: boolean
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
  const filterState = useCatalogFilterState({ filters, onFilterChange })
  const { searchInput, setSearchInput } = filterState

  const handleTagToggle = (tag: string) => {
    const newTags = filters.tags.includes(tag)
      ? filters.tags.filter((t) => t !== tag)
      : [...filters.tags, tag]
    onFilterChange({ tags: newTags, page: 1 })
  }

  const categoryLabel = filters.categoryId
    ? (categories.find((c) => c.id === filters.categoryId)?.name ?? 'Category')
    : 'All categories'

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
              <DropdownMenuItem onSelect={() => onFilterChange({ categoryId: '', page: 1 })}>
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
                <Label htmlFor={`tag-${tag}`} className="text-xs cursor-pointer font-normal">
                  {tag}
                </Label>
              </div>
            ))}
          </div>
        )}
      </div>

      <CatalogPriceFilter state={filterState} />

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
  )
}
