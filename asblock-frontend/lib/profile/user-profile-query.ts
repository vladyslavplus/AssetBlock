import type { AssetListItem } from "@/lib/catalog/asset-types";
import { apiFetch } from "@/lib/http/api-client";
import type { AssetListItemApi, PagedResultDto } from "@/lib/catalog/assets-api";
import { mapApiAssetToListItem } from "@/lib/catalog/assets-api";
import { CATALOG_ASSETS_PAGE_SIZE } from "@/lib/catalog/catalog-filters";
import type { AuthorCatalogPageResult } from "@/lib/server/user-profile-server";

export const userProfileKeys = {
  all: ["userProfile"] as const,
  authorCatalog: (authorId: string, page: number) =>
    [...userProfileKeys.all, "authorCatalog", authorId, page] as const,
};

export type { AuthorCatalogPageResult };

export async function fetchAuthorCatalogClient(
  authorId: string,
  page: number,
): Promise<AuthorCatalogPageResult> {
  const safePage = Math.max(1, Math.floor(page));
  const qs = new URLSearchParams({
    page: String(safePage),
    pageSize: String(CATALOG_ASSETS_PAGE_SIZE),
    sortBy: "CreatedAt",
    sortDirection: "DESC",
    authorId,
  });
  const data = await apiFetch<PagedResultDto<AssetListItemApi>>({
    path: `api/assets?${qs.toString()}`,
    method: "GET",
  });
  const totalCount = data.totalCount ?? 0;
  const totalPages =
    CATALOG_ASSETS_PAGE_SIZE > 0 ? Math.ceil(totalCount / CATALOG_ASSETS_PAGE_SIZE) : 0;
  const items: AssetListItem[] = (data.items ?? []).map(mapApiAssetToListItem);
  return {
    items,
    totalCount,
    page: data.page ?? safePage,
    pageSize: CATALOG_ASSETS_PAGE_SIZE,
    totalPages,
  };
}
