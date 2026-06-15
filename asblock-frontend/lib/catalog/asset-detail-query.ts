import { apiFetch } from "@/lib/http/api-client";
import type { AssetDetailItemApi, PagedResultDto, ReviewListItemApi } from "@/lib/catalog/assets-api";
import { mapReviewApiToUi } from "@/lib/catalog/assets-api";
import type { AssetReview } from "@/lib/catalog/catalog-utils";

export const assetKeys = {
  all: ["assets"] as const,
  detail: (id: string) => [...assetKeys.all, "detail", id] as const,
  reviews: (id: string) => [...assetKeys.all, "reviews", id] as const,
};

export async function fetchAssetDetailPublic(assetId: string): Promise<AssetDetailItemApi> {
  return apiFetch<AssetDetailItemApi>({
    path: `api/assets/${encodeURIComponent(assetId)}`,
    method: "GET",
  });
}

export async function fetchAssetReviewsPublic(assetId: string): Promise<AssetReview[]> {
  const qs = new URLSearchParams({
    page: "1",
    pageSize: "50",
    sortBy: "CreatedAt",
    sortDirection: "DESC",
  });
  const data = await apiFetch<PagedResultDto<ReviewListItemApi>>({
    path: `api/reviews/assets/${encodeURIComponent(assetId)}/reviews?${qs.toString()}`,
    method: "GET",
  });
  return (data.items ?? []).map(mapReviewApiToUi);
}
