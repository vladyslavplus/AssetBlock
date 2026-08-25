import { z } from 'zod'
import { getApiErrorMessage } from '@/lib/http/api-errors'
import { isAbortError, toAbortError } from '@/lib/http/is-abort-error'
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

async function fetchJson(url: string, signal?: AbortSignal): Promise<Response> {
  try {
    return await fetch(url, { credentials: 'include', signal })
  } catch (error) {
    if (isAbortError(error, signal)) throw toAbortError(error, signal)
    throw error
  }
}

async function readResponseText(res: Response, signal?: AbortSignal): Promise<string> {
  try {
    return await res.text()
  } catch (error) {
    if (isAbortError(error, signal)) throw toAbortError(error, signal)
    throw error
  }
}

export async function fetchAssetProcessingJobs(
  assetId: string,
  signal?: AbortSignal,
): Promise<AssetProcessingJobDto[]> {
  const res = await fetchJson(
    `/api/seller/assets/${encodeURIComponent(assetId)}/processing-jobs`,
    signal,
  )
  const text = await readResponseText(res, signal)
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
  const res = await fetchJson(
    `/api/seller/asset-versions/${encodeURIComponent(assetVersionId)}/processing-jobs`,
    signal,
  )
  const text = await readResponseText(res, signal)
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
