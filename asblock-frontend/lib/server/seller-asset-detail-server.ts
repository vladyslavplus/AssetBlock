import { cookies } from 'next/headers'
import { cache } from 'react'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { sellerAssetDetailSchema, type SellerAssetDetail } from '@/lib/seller/seller-asset-schemas'

export type SellerAssetDetailLookup =
  | { status: 'ok'; asset: SellerAssetDetail }
  | { status: 'unauthorized' }
  | { status: 'not_found' }

async function readSellerAssetDetail(id: string): Promise<SellerAssetDetailLookup> {
  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/users/me/assets/${encodeURIComponent(id)}`,
    {
      method: 'GET',
    },
  )
  if (res.status === 401) {
    return { status: 'unauthorized' }
  }
  if (res.status === 404) {
    return { status: 'not_found' }
  }
  if (!res.ok) {
    throw new Error(`Seller asset fetch failed: ${res.status}`)
  }
  const text = await res.text()
  const parsed = sellerAssetDetailSchema.safeParse(text ? JSON.parse(text) : undefined)
  if (!parsed.success) {
    throw new Error('Seller asset response was invalid.')
  }
  return { status: 'ok', asset: parsed.data }
}

export const getSellerAssetDetailForRequest = cache(readSellerAssetDetail)
