import { apiFetch } from "@/lib/http/api-client";
import { getApiErrorMessage, parseApiErrorBody } from "@/lib/http/api-errors";
import type { AssetListItemApi, PagedResultDto, TagDtoApi } from "@/lib/catalog/assets-api";

const TAG_PAGE_SIZE = 100;

function parseMaybeJson(text: string): unknown {
  if (!text) return undefined;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return text;
  }
}

export async function fetchMyListings(signal?: AbortSignal): Promise<PagedResultDto<AssetListItemApi>> {
  const params = new URLSearchParams({
    page: "1",
    pageSize: "50",
    sortBy: "CreatedAt",
    sortDirection: "DESC",
  });
  const res = await fetch(`/api/seller/listings?${params.toString()}`, {
    credentials: "include",
    signal,
  });
  const text = await res.text();
  const parsed = parseMaybeJson(text);
  if (res.status === 401) {
    throw new Error("SIGN_IN_REQUIRED");
  }
  if (!res.ok) {
    const msg = getApiErrorMessage(parsed, `Could not load listings (${res.status})`);
    throw new Error(msg);
  }
  return parsed as PagedResultDto<AssetListItemApi>;
}

export type UploadAssetResult =
  | { ok: true; assetId: string }
  | { ok: false; message: string; fieldErrors?: Record<string, string> };

export async function uploadSellerAsset(formData: FormData): Promise<UploadAssetResult> {
  const res = await fetch("/api/seller/upload", {
    method: "POST",
    body: formData,
    credentials: "include",
  });
  const text = await res.text();
  const parsed = parseMaybeJson(text);
  if (!res.ok) {
    const p = parseApiErrorBody(parsed);
    const fe = p?.fieldErrors;
    const keys = fe ? Object.keys(fe) : [];
    return {
      ok: false,
      message: p?.summary ?? `Upload failed (${res.status})`,
      ...(keys.length > 0 && fe ? { fieldErrors: fe } : {}),
    };
  }
  if (
    typeof parsed === "object" &&
    parsed !== null &&
    "id" in parsed &&
    typeof (parsed as { id: unknown }).id === "string"
  ) {
    return { ok: true, assetId: (parsed as { id: string }).id };
  }
  return { ok: false, message: "Unexpected response from server." };
}

export type SellerMutationResult =
  | { ok: true }
  | { ok: false; message: string; fieldErrors?: Record<string, string> };

export interface PatchSellerAssetBody {
  title: string;
  /** Empty string clears description on the server. Omit undefined fields only if we sent partial — here we send full shape. */
  description: string | null;
  price: number;
  categoryId: string;
}

export async function patchSellerAsset(
  assetId: string,
  body: PatchSellerAssetBody,
): Promise<SellerMutationResult> {
  const res = await fetch(`/api/seller/assets/${encodeURIComponent(assetId)}`, {
    method: "PATCH",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      title: body.title,
      description: body.description,
      price: body.price,
      categoryId: body.categoryId,
    }),
  });
  const parsed = parseMaybeJson(await res.text());
  if (!res.ok) {
    const p = parseApiErrorBody(parsed);
    const fe = p?.fieldErrors;
    const keys = fe ? Object.keys(fe) : [];
    return {
      ok: false,
      message: p?.summary ?? `Could not update asset (${res.status})`,
      ...(keys.length > 0 && fe ? { fieldErrors: fe } : {}),
    };
  }
  return { ok: true };
}

export async function deleteSellerAsset(assetId: string): Promise<SellerMutationResult> {
  const res = await fetch(`/api/seller/assets/${encodeURIComponent(assetId)}`, {
    method: "DELETE",
    credentials: "include",
  });
  const parsed = parseMaybeJson(await res.text());
  if (!res.ok) {
    return {
      ok: false,
      message: getApiErrorMessage(parsed, `Could not delete asset (${res.status})`),
    };
  }
  return { ok: true };
}

export async function addSellerAssetTag(assetId: string, name: string): Promise<SellerMutationResult> {
  const res = await fetch(`/api/seller/assets/${encodeURIComponent(assetId)}/tags`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name: name.trim() }),
  });
  const parsed = parseMaybeJson(await res.text());
  if (!res.ok) {
    return {
      ok: false,
      message: getApiErrorMessage(parsed, `Could not add tag "${name.trim()}" (${res.status})`),
    };
  }
  return { ok: true };
}

export async function removeSellerAssetTag(
  assetId: string,
  tagId: string,
): Promise<SellerMutationResult> {
  const res = await fetch(
    `/api/seller/assets/${encodeURIComponent(assetId)}/tags/${encodeURIComponent(tagId)}`,
    { method: "DELETE", credentials: "include" },
  );
  const parsed = parseMaybeJson(await res.text());
  if (!res.ok) {
    return {
      ok: false,
      message: getApiErrorMessage(parsed, `Could not remove tag (${res.status})`),
    };
  }
  return { ok: true };
}

/**
 * Lowercase tag name → id (public catalog tags API).
 */
export async function fetchTagNameToIdMap(): Promise<Map<string, string>> {
  const map = new Map<string, string>();
  let page = 1;
  for (;;) {
    const data = await apiFetch<PagedResultDto<TagDtoApi>>({
      path: `api/tags?page=${page}&pageSize=${TAG_PAGE_SIZE}&sortBy=name&sortDirection=ASC`,
      method: "GET",
    });
    const batch = data.items ?? [];
    for (const t of batch) {
      map.set(t.name.trim().toLowerCase(), t.id);
    }
    if (batch.length === 0 || page * TAG_PAGE_SIZE >= data.totalCount) {
      break;
    }
    page += 1;
  }
  return map;
}

function parseTagsCsv(csv: string | undefined): string[] {
  return (csv ?? "")
    .split(/[,;\n]+/)
    .map((t) => t.trim().toLowerCase())
    .filter(Boolean);
}

/**
 * Syncs tag links after metadata PATCH: only tags that exist in the global catalog can be added.
 */
export async function syncSellerAssetTags(
  assetId: string,
  previousLowerNames: string[],
  tagsCsv: string | undefined,
  tagLookup: Map<string, string>,
): Promise<SellerMutationResult> {
  const prev = new Set(previousLowerNames.map((n) => n.trim().toLowerCase()).filter(Boolean));
  const next = new Set(parseTagsCsv(tagsCsv));
  const toRemove = [...prev].filter((t) => !next.has(t));
  const toAdd = [...next].filter((t) => !prev.has(t));

  for (const name of toRemove) {
    const tagId = tagLookup.get(name);
    if (!tagId) {
      return {
        ok: false,
        message: `Cannot remove tag "${name}" — tag id not found. Try saving again after refresh.`,
      };
    }
    const r = await removeSellerAssetTag(assetId, tagId);
    if (!r.ok) return r;
  }

  for (const name of toAdd) {
    const r = await addSellerAssetTag(assetId, name);
    if (!r.ok) {
      return {
        ok: false,
        message: r.message.includes(name) ? r.message : `${r.message} (tag: "${name}")`,
      };
    }
  }

  return { ok: true };
}
