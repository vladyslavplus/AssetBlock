import endpoints from './seller-review-endpoints.json'

// Also consumed by real HTTP acceptance, so BFF transport cannot diverge from that check.
export const sellerReviewBackendEndpoints = endpoints
export function sellerReviewBackendPath(
  kind: keyof typeof endpoints,
  assetId: string,
  versionId: string,
) {
  return endpoints[kind].path
    .replace('{assetId}', encodeURIComponent(assetId))
    .replace('{versionId}', encodeURIComponent(versionId))
}
