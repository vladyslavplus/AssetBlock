export const CATALOG_ASSETS_PAGE_SIZE = 12;

export interface CatalogFilters {
  search: string;
  categoryId: string;
  tags: string[];
  minPrice: number | null;
  maxPrice: number | null;
  sortBy: "CreatedAt" | "Title" | "Price";
  sortDirection: "ASC" | "DESC";
  page: number;
  pageSize: number;
}

export const DEFAULT_CATALOG_FILTERS: CatalogFilters = {
  search: "",
  categoryId: "",
  tags: [],
  minPrice: null,
  maxPrice: null,
  sortBy: "CreatedAt",
  sortDirection: "DESC",
  page: 1,
  pageSize: CATALOG_ASSETS_PAGE_SIZE,
};

export function sortDirectionForSortBy(sortBy: CatalogFilters["sortBy"]): "ASC" | "DESC" {
  if (sortBy === "CreatedAt") return "DESC";
  return "ASC";
}

export const CATALOG_SORT_OPTIONS: Array<{ value: CatalogFilters["sortBy"]; label: string }> = [
  { value: "CreatedAt", label: "Newest" },
  { value: "Title", label: "Title A–Z" },
  { value: "Price", label: "Price: low to high" },
];

export function getCatalogSortLabel(sortBy: CatalogFilters["sortBy"]): string {
  return CATALOG_SORT_OPTIONS.find((o) => o.value === sortBy)?.label ?? sortBy;
}
