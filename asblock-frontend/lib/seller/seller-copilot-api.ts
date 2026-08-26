import { fetchBffJson } from '@/lib/http/bff-json'
import { getApiErrorMessage, parseApiErrorBody } from '@/lib/http/api-errors'
import { isAbortError, toAbortError } from '@/lib/http/is-abort-error'
import {
  listingCopilotEnqueueResponseSchema,
  listingCopilotSuggestionSchema,
  type ListingCopilotEnqueueResponse,
  type ListingCopilotSuggestion,
} from '@/lib/seller/seller-copilot-schemas'

export class ListingCopilotRequestError extends Error {
  readonly status: number
  readonly code?: string

  constructor(message: string, status: number, code?: string) {
    super(message)
    this.name = 'ListingCopilotRequestError'
    this.status = status
    this.code = code
  }
}

export async function fetchListingCopilotSuggestion(
  assetVersionId: string,
  signal?: AbortSignal,
): Promise<ListingCopilotSuggestion | null> {
  let response: Response
  try {
    response = await fetch(
      `/api/seller/asset-versions/${encodeURIComponent(assetVersionId)}/listing-copilot`,
      { method: 'GET', credentials: 'include', signal },
    )
  } catch (error) {
    if (isAbortError(error, signal)) throw toAbortError(error, signal)
    throw error
  }

  if (response.status === 404) {
    return null
  }

  let text: string
  try {
    text = await response.text()
  } catch (error) {
    if (isAbortError(error, signal)) throw toAbortError(error, signal)
    throw error
  }
  let body: unknown
  try {
    body = text ? JSON.parse(text) : undefined
  } catch {
    body = text
  }

  if (!response.ok) {
    throw new ListingCopilotRequestError(
      getApiErrorMessage(body, 'Could not load the AI suggestion.'),
      response.status,
      parseApiErrorBody(body)?.code,
    )
  }

  const parsed = listingCopilotSuggestionSchema.safeParse(body)
  if (!parsed.success) {
    throw new ListingCopilotRequestError('Could not load the AI suggestion.', response.status)
  }

  return parsed.data
}

export async function enqueueListingCopilot(
  assetVersionId: string,
  signal?: AbortSignal,
): Promise<ListingCopilotEnqueueResponse> {
  const result = await fetchBffJson(
    `/api/seller/asset-versions/${encodeURIComponent(assetVersionId)}/listing-copilot`,
    listingCopilotEnqueueResponseSchema,
    { method: 'POST', signal },
  )

  if (!result.ok) {
    throw new ListingCopilotRequestError(
      result.message,
      result.status,
      parseApiErrorBody(result.body)?.code,
    )
  }

  return result.data
}
