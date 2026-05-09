import { apiFetch } from "@/lib/api-client";
import type { AssetListItem } from "@/lib/asset-types";
import type { AssetReview } from "@/lib/catalog-utils";
import { CATALOG_ASSETS_PAGE_SIZE, type CatalogFilters } from "@/lib/catalog-filters";

const API_MAX_PAGE_SIZE = 100;

export interface PagedResultDto<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface AssetListItemApi {
  id: string;
  title: string;
  description: string | null;
  price: number;
  categoryId: string;
  categoryName: string | null;
  authorId: string;
  authorUsername: string;
  createdAt: string;
  tags: string[];
  averageRating: number;
}

export function mapApiAssetToListItem(row: AssetListItemApi): AssetListItem {
  return {
    id: row.id,
    title: row.title,
    description: row.description,
    price: Number(row.price),
    categoryId: row.categoryId,
    categoryName: row.categoryName,
    authorId: row.authorId,
    authorUsername: row.authorUsername,
    createdAt: row.createdAt,
    tags: row.tags ?? [],
    averageRating: Number(row.averageRating),
  };
}

export function buildAssetsQueryParams(filters: CatalogFilters): string {
  const p = new URLSearchParams();
  p.set("page", String(filters.page));
  // Catalog always requests a fixed page size (API default may be 10).
  p.set("pageSize", String(CATALOG_ASSETS_PAGE_SIZE));
  p.set("sortBy", filters.sortBy);
  p.set("sortDirection", filters.sortDirection);
  const q = filters.search.trim();
  if (q) p.set("search", q);
  if (filters.categoryId) p.set("categoryId", filters.categoryId);
  for (const t of filters.tags) {
    const s = t.trim();
    if (s) p.append("tags", s);
  }
  if (filters.minPrice != null) p.set("minPrice", String(filters.minPrice));
  if (filters.maxPrice != null) p.set("maxPrice", String(filters.maxPrice));
  return p.toString();
}

export interface FetchAssetsPageResult {
  items: AssetListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export type CatalogFetchInit = Pick<RequestInit, "signal" | "headers" | "credentials">;

export async function fetchAssetsPage(
  filters: CatalogFilters,
  init?: CatalogFetchInit,
): Promise<FetchAssetsPageResult> {
  const qs = buildAssetsQueryParams(filters);
  const data = await apiFetch<PagedResultDto<AssetListItemApi>>({
    path: `api/assets?${qs}`,
    method: "GET",
    ...init,
  });
  const totalPages =
    CATALOG_ASSETS_PAGE_SIZE > 0
      ? Math.ceil(data.totalCount / CATALOG_ASSETS_PAGE_SIZE)
      : 0;
  return {
    items: (data.items ?? []).map(mapApiAssetToListItem),
    totalCount: data.totalCount,
    page: data.page,
    pageSize: CATALOG_ASSETS_PAGE_SIZE,
    totalPages,
  };
}

export interface CategoryListItemApi {
  id: string;
  name: string;
  slug: string;
  description: string | null;
}

export async function fetchCategoryOptions(init?: CatalogFetchInit): Promise<Array<{ id: string; name: string }>> {
  const out: Array<{ id: string; name: string }> = [];
  let page = 1;
  const maxPages = 50;
  while (page <= maxPages) {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(API_MAX_PAGE_SIZE),
      sortBy: "Name",
      sortDirection: "ASC",
    });
    const data = await apiFetch<PagedResultDto<CategoryListItemApi>>({
      path: `api/categories?${params.toString()}`,
      method: "GET",
      ...init,
    });
    const batch = data.items ?? [];
    out.push(...batch.map((c) => ({ id: c.id, name: c.name })));
    if (out.length >= data.totalCount || batch.length < API_MAX_PAGE_SIZE) break;
    page += 1;
  }
  return out;
}

export interface TagDtoApi {
  id: string;
  name: string;
}

export async function fetchTagNamesForFilters(init?: CatalogFetchInit): Promise<string[]> {
  const names: string[] = [];
  let page = 1;
  const maxPages = 50;
  while (page <= maxPages) {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(API_MAX_PAGE_SIZE),
      sortBy: "name",
      sortDirection: "ASC",
    });
    const data = await apiFetch<PagedResultDto<TagDtoApi>>({
      path: `api/tags?${params.toString()}`,
      method: "GET",
      ...init,
    });
    const batch = data.items ?? [];
    names.push(...batch.map((t) => t.name));
    if (names.length >= data.totalCount || batch.length < API_MAX_PAGE_SIZE) break;
    page += 1;
  }
  return names.sort((a, b) => a.localeCompare(b));
}

export interface AssetDetailItemApi {
  id: string;
  title: string;
  description: string | null;
  price: number;
  categoryId: string;
  categoryName: string | null;
  authorId: string;
  authorUsername: string;
  createdAt: string;
  updatedAt: string | null;
  tags: string[];
  averageRating: number;
}

/** Shapes list item for components that still expect AssetListItem (detail lacks tags/rating/username). */
export function mapDetailApiToListItemForHero(row: AssetDetailItemApi): AssetListItem {
  return {
    id: row.id,
    title: row.title,
    description: row.description,
    price: Number(row.price),
    categoryId: row.categoryId,
    categoryName: row.categoryName,
    authorId: row.authorId,
    authorUsername: row.authorUsername?.trim() || "Creator",
    createdAt: row.createdAt,
    tags: Array.isArray(row.tags) ? row.tags : [],
    averageRating: Number(row.averageRating) || 0,
  };
}

export interface ReviewListItemApi {
  id: string;
  assetId: string;
  userId: string;
  username: string | null;
  rating: number;
  comment: string | null;
  createdAt: string;
}

export function mapReviewApiToUi(row: ReviewListItemApi): AssetReview {
  return {
    id: row.id,
    authorUsername: row.username?.trim() || "user",
    rating: row.rating,
    body: row.comment?.trim() ?? "",
    createdAt: row.createdAt,
    verifiedPurchase: false,
  };
}

export interface FetchFeaturedAssetsOptions {
  limit?: number;
}

/**
 * Latest assets for marketing surfaces (landing featured strip).
 * Uses public GET /api/assets — no auth.
 */
export async function fetchFeaturedAssets(
  options: FetchFeaturedAssetsOptions = {},
): Promise<AssetListItem[]> {
  const limit = options.limit ?? 8;
  const params = new URLSearchParams({
    page: "1",
    pageSize: String(limit),
    sortBy: "CreatedAt",
    sortDirection: "DESC",
  });

  const data = await apiFetch<PagedResultDto<AssetListItemApi>>({
    path: `api/assets?${params.toString()}`,
    method: "GET",
  });

  return (data.items ?? []).map(mapApiAssetToListItem);
}
