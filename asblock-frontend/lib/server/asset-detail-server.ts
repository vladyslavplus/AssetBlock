import { cache } from "react";
import { getServerApiBaseUrl } from "@/lib/http/api-config";
import type { AssetDetailItemApi, PagedResultDto, ReviewListItemApi } from "@/lib/catalog/assets-api";
import { mapReviewApiToUi } from "@/lib/catalog/assets-api";
import type { AssetReview } from "@/lib/catalog/catalog-utils";

async function readJson<T>(res: Response): Promise<T | undefined> {
  const text = await res.text();
  if (!text) return undefined;
  return JSON.parse(text) as T;
}

export const getAssetDetailCached = cache(async (id: string): Promise<AssetDetailItemApi | null> => {
  const base = getServerApiBaseUrl();
  const res = await fetch(`${base}/api/assets/${encodeURIComponent(id)}`, { cache: "no-store" });
  if (res.status === 404) return null;
  if (!res.ok) {
    throw new Error(`Asset fetch failed: ${res.status}`);
  }
  const body = await readJson<AssetDetailItemApi>(res);
  return body ?? null;
});

export const getAssetReviewsCached = cache(async (assetId: string): Promise<AssetReview[]> => {
  const base = getServerApiBaseUrl();
  const qs = new URLSearchParams({
    page: "1",
    pageSize: "50",
    sortBy: "CreatedAt",
    sortDirection: "DESC",
  });
  const res = await fetch(
    `${base}/api/reviews/assets/${encodeURIComponent(assetId)}/reviews?${qs.toString()}`,
    { cache: "no-store" },
  );
  if (!res.ok) {
    return [];
  }
  const data = await readJson<PagedResultDto<ReviewListItemApi>>(res);
  if (!data) return [];
  return (data.items ?? []).map(mapReviewApiToUi);
});
