import { AssetsBrowsePage } from '@/components/assets/assets-browse-page'
import { parseCatalogUrlParams } from '@/lib/catalog/catalog-url-state'
import { getCatalogFacetsCached, getCatalogPageCached } from '@/lib/server/catalog-server'

interface AssetsPageProps {
  searchParams: Promise<Record<string, string | string[] | undefined>>
}

export default async function AssetsPage({ searchParams }: AssetsPageProps) {
  const rawParams = await searchParams
  const filters = parseCatalogUrlParams(rawParams)

  // Fetch initial catalog page and facets in parallel on the server
  const [initialAssetsResult, initialFacets] = await Promise.all([
    getCatalogPageCached(filters),
    getCatalogFacetsCached(),
  ])

  return (
    <AssetsBrowsePage
      initialFilters={filters}
      initialAssetsResult={initialAssetsResult}
      initialFacets={initialFacets}
    />
  )
}
