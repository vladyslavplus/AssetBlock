import { z } from 'zod'
import { getApiErrorMessage } from '@/lib/http/api-errors'
import { isAbortError } from '@/lib/http/is-abort-error'
import {
  assetProcessingJobSchema,
  type AssetProcessingJobDto,
} from '@/lib/seller/seller-processing-schemas'

function parseMaybeJson(text: string): unknown {
  if (!text) return undefined
  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

export async function fetchAssetProcessingJobs(
  assetId: string,
  signal?: AbortSignal,
): Promise<AssetProcessingJobDto[]> {
  const res = await fetch(`/api/seller/assets/${encodeURIComponent(assetId)}/processing-jobs`, {
    credentials: 'include',
    signal,
  })
  let text: string
  try {
    text = await res.text()
  } catch (error) {
    if (isAbortError(error, signal)) throw error
    throw error
  }
  const parsed = parseMaybeJson(text)
  if (res.status === 401) {
    throw new Error('SIGN_IN_REQUIRED')
  }
  if (!res.ok) {
    const msg = getApiErrorMessage(parsed, `Could not load processing jobs (${res.status})`)
    throw new Error(msg)
  }
  const validated = z.array(assetProcessingJobSchema).safeParse(parsed)
  if (!validated.success) {
    throw new Error('Could not load processing jobs.')
  }
  return validated.data
}

export async function fetchAssetVersionProcessingJobs(
  assetVersionId: string,
  signal?: AbortSignal,
): Promise<AssetProcessingJobDto[]> {
  const res = await fetch(
    `/api/seller/asset-versions/${encodeURIComponent(assetVersionId)}/processing-jobs`,
    {
      credentials: 'include',
      signal,
    },
  )
  let text: string
  try {
    text = await res.text()
  } catch (error) {
    if (isAbortError(error, signal)) throw error
    throw error
  }
  const parsed = parseMaybeJson(text)
  if (res.status === 401) {
    throw new Error('SIGN_IN_REQUIRED')
  }
  if (!res.ok) {
    const msg = getApiErrorMessage(parsed, `Could not load processing jobs (${res.status})`)
    throw new Error(msg)
  }
  const validated = z.array(assetProcessingJobSchema).safeParse(parsed)
  if (!validated.success) {
    throw new Error('Could not load processing jobs.')
  }
  return validated.data
}
