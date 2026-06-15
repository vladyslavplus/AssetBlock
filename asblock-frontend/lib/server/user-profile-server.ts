import { CATALOG_ASSETS_PAGE_SIZE } from "@/lib/catalog/catalog-filters";
import { getServerApiBaseUrl } from "@/lib/http/api-config";
import type { PagedResultDto, AssetListItemApi } from "@/lib/catalog/assets-api";
import { mapApiAssetToListItem } from "@/lib/catalog/assets-api";
import type { AssetListItem } from "@/lib/catalog/asset-types";
import type { UserProfilePublic } from "@/lib/profile/public-profile-types";

export async function fetchPublicProfileByUsername(username: string): Promise<UserProfilePublic | null> {
  const trimmed = username.trim();
  if (!trimmed) {
    return null;
  }
  const base = getServerApiBaseUrl();
  const res = await fetch(`${base}/api/users/${encodeURIComponent(trimmed)}`, { cache: "no-store" });
  if (res.status === 404) {
    return null;
  }
  if (!res.ok) {
    return null;
  }
  return (await res.json()) as UserProfilePublic;
}

export interface AuthorCatalogPageResult {
  items: AssetListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export async function fetchAuthorAssetsPage(authorId: string, page: number): Promise<AuthorCatalogPageResult> {
  const safePage = Math.max(1, Math.floor(page));
  const qs = new URLSearchParams({
    page: String(safePage),
    pageSize: String(CATALOG_ASSETS_PAGE_SIZE),
    sortBy: "CreatedAt",
    sortDirection: "DESC",
    authorId,
  });
  const base = getServerApiBaseUrl();
  const res = await fetch(`${base}/api/assets?${qs.toString()}`, { cache: "no-store" });
  if (!res.ok) {
    return {
      items: [],
      totalCount: 0,
      page: safePage,
      pageSize: CATALOG_ASSETS_PAGE_SIZE,
      totalPages: 0,
    };
  }
  const data = (await res.json()) as PagedResultDto<AssetListItemApi>;
  const totalCount = data.totalCount ?? 0;
  const totalPages =
    CATALOG_ASSETS_PAGE_SIZE > 0 ? Math.ceil(totalCount / CATALOG_ASSETS_PAGE_SIZE) : 0;
  return {
    items: (data.items ?? []).map(mapApiAssetToListItem),
    totalCount,
    page: data.page ?? safePage,
    pageSize: CATALOG_ASSETS_PAGE_SIZE,
    totalPages,
  };
}
