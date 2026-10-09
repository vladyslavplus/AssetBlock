import { getApiErrorMessage, parseApiErrorBody } from '@/lib/http/api-errors'
import { isAbortError, toAbortError } from '@/lib/http/is-abort-error'
import {
  sellerDraftCreatedSchema,
  sellerDraftSnapshotSchema,
  sellerVersionReviewSchema,
  draftSaveResultSchema,
  sellerDeclarationSnapshotSchema,
  type SellerDraftSnapshot,
  type SellerDeclarationSnapshot,
  type SellerVersionReview,
  type DraftSaveResult,
  type SellerDraftCreated,
} from '@/lib/seller/seller-draft-schemas'

function parseMaybeJson(text: string): unknown {
  if (!text) return undefined
  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

async function fetchJson(url: string, init: RequestInit = {}): Promise<Response> {
  try {
    return await fetch(url, init)
  } catch (error) {
    if (isAbortError(error, init.signal)) throw toAbortError(error, init.signal)
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

export type SellerApiResult<T> =
  | { ok: true; value: T }
  | {
      ok: false
      message: string
      fieldErrors?: Record<string, string>
      code?: string
    }

function errorResult(parsed: unknown, fallback: string) {
  const p = parseApiErrorBody(parsed)
  const fe = p?.fieldErrors
  const keys = fe ? Object.keys(fe) : []
  return {
    ok: false as const,
    message: p?.summary ?? fallback,
    ...(p?.code ? { code: p.code } : {}),
    ...(keys.length > 0 && fe ? { fieldErrors: fe } : {}),
  }
}

async function sendJson<T>(
  url: string,
  method: 'POST' | 'PATCH' | 'PUT',
  body: unknown,
  schema: { safeParse(input: unknown): { success: true; data: T } | { success: false } },
): Promise<SellerApiResult<T>> {
  const res = await fetchJson(url, {
    method,
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const parsed = parseMaybeJson(await res.text())
  if (!res.ok) {
    return errorResult(parsed, `Request failed (${res.status})`)
  }
  if (!schema.safeParse(parsed).success) {
    return { ok: false, message: 'Unexpected response from server.' }
  }
  return { ok: true, value: parsed as T }
}

export async function createSellerDraft(body: {
  // Created once per logical operation by the caller and reused on retry, so a
  // lost response replays the committed draft instead of creating a second one.
  operationId: string
  title: string
  description: string | null
  price: number
  categoryId: string
  downloadLimitPerHour: number | null
}): Promise<SellerApiResult<SellerDraftCreated>> {
  const res = await fetchJson('/api/seller/drafts', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const parsed = parseMaybeJson(await res.text())
  if (!res.ok) {
    return errorResult(parsed, `Could not create draft (${res.status})`)
  }
  const created = sellerDraftCreatedSchema.safeParse(parsed)
  if (!created.success) {
    return { ok: false, message: 'Unexpected response from server.' }
  }
  return { ok: true, value: created.data }
}

export async function fetchSellerDraft(
  assetId: string,
  signal?: AbortSignal,
): Promise<SellerDraftSnapshot> {
  const res = await fetchJson(`/api/seller/assets/${encodeURIComponent(assetId)}/draft`, {
    credentials: 'include',
    signal,
  })
  const text = await readResponseText(res, signal)
  const parsed = parseMaybeJson(text)
  if (res.status === 401) {
    throw new Error('SIGN_IN_REQUIRED')
  }
  if (res.status === 404) {
    throw new Error('NOT_FOUND')
  }
  if (!res.ok) {
    throw new Error(getApiErrorMessage(parsed, `Could not load draft (${res.status})`))
  }
  const snapshot = sellerDraftSnapshotSchema.safeParse(parsed)
  if (!snapshot.success) {
    throw new Error('Draft response was invalid.')
  }
  return snapshot.data
}

export async function fetchSellerDeclaration(
  assetId: string,
  versionId: string | null,
  signal?: AbortSignal,
): Promise<SellerDeclarationSnapshot> {
  const suffix = versionId
    ? `/versions/${encodeURIComponent(versionId)}/declaration`
    : '/declaration'
  const res = await fetchJson(`/api/seller/assets/${encodeURIComponent(assetId)}${suffix}`, {
    credentials: 'include',
    signal,
  })
  const text = await readResponseText(res, signal)
  const parsed = parseMaybeJson(text)
  if (res.status === 401) {
    throw new Error('SIGN_IN_REQUIRED')
  }
  if (res.status === 404) {
    throw new Error('NOT_FOUND')
  }
  if (!res.ok) {
    throw new Error(getApiErrorMessage(parsed, `Could not load declaration (${res.status})`))
  }
  const snapshot = sellerDeclarationSnapshotSchema.safeParse(parsed)
  if (!snapshot.success) {
    throw new Error('Declaration response was invalid.')
  }
  return snapshot.data
}

export async function fetchSellerVersionReview(
  assetId: string,
  versionId: string,
  signal?: AbortSignal,
): Promise<SellerVersionReview> {
  const res = await fetchJson(
    `/api/seller/assets/${encodeURIComponent(assetId)}/versions/${encodeURIComponent(versionId)}/review`,
    { credentials: 'include', signal },
  )
  const text = await readResponseText(res, signal)
  const parsed = parseMaybeJson(text)
  if (res.status === 401) {
    throw new Error('SIGN_IN_REQUIRED')
  }
  if (res.status === 404) {
    throw new Error('NOT_FOUND')
  }
  if (!res.ok) {
    throw new Error(getApiErrorMessage(parsed, `Could not load version review (${res.status})`))
  }
  const review = sellerVersionReviewSchema.safeParse(parsed)
  if (!review.success) {
    throw new Error('Version review response was invalid.')
  }
  return review.data
}

export async function saveSellerDraft(
  assetId: string,
  body: { operationId: string; expectedWorkspaceRevision: number; material: unknown },
): Promise<SellerApiResult<DraftSaveResult>> {
  return sendJson(
    `/api/seller/assets/${encodeURIComponent(assetId)}/draft`,
    'PATCH',
    body,
    draftSaveResultSchema,
  )
}

export async function saveSellerDeclaration(
  assetId: string,
  versionId: string | null,
  body: { operationId: string; expectedWorkspaceRevision: number; declaration: unknown },
): Promise<SellerApiResult<DraftSaveResult>> {
  const suffix = versionId
    ? `/versions/${encodeURIComponent(versionId)}/declaration`
    : '/declaration'
  return sendJson(
    `/api/seller/assets/${encodeURIComponent(assetId)}${suffix}`,
    'PUT',
    body,
    draftSaveResultSchema,
  )
}

export async function updateSellerAssetPrice(
  assetId: string,
  price: number,
): Promise<SellerApiResult<null>> {
  const res = await fetchJson(`/api/seller/assets/${encodeURIComponent(assetId)}/price`, {
    method: 'PATCH',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ price }),
  })
  const parsed = parseMaybeJson(await res.text())
  if (!res.ok) {
    const p = parseApiErrorBody(parsed)
    const fe = p?.fieldErrors
    const keys = fe ? Object.keys(fe) : []
    return {
      ok: false,
      message: p?.summary ?? `Could not update price (${res.status})`,
      ...(keys.length > 0 && fe ? { fieldErrors: fe } : {}),
    }
  }
  return { ok: true, value: null }
}

export type SubmitVersionResult =
  | { ok: true; submissionId: string }
  | { ok: false; message: string; blockedReasons?: string[] }

export async function submitSellerAssetVersion(
  assetId: string,
  versionId: string,
  body: {
    workspaceId: string
    expectedWorkspaceRevision: number
    expectedCaseRevision: number
    operationId: string
  },
): Promise<SubmitVersionResult> {
  const res = await fetchJson(
    `/api/seller/assets/${encodeURIComponent(assetId)}/versions/${encodeURIComponent(versionId)}/submissions`,
    {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  )
  const parsed = parseMaybeJson(await res.text())
  if (res.status === 409) {
    const p = parseApiErrorBody(parsed)
    return {
      ok: false,
      message: p?.summary ?? 'Submission blocked.',
      ...(p?.code ? { blockedReasons: [p.code] } : {}),
    }
  }
  if (!res.ok) {
    const p = parseApiErrorBody(parsed)
    return { ok: false, message: p?.summary ?? `Could not submit version (${res.status})` }
  }
  if (
    typeof parsed === 'object' &&
    parsed !== null &&
    'id' in parsed &&
    typeof (parsed as { id: unknown }).id === 'string'
  ) {
    return { ok: true, submissionId: (parsed as { id: string }).id }
  }
  return { ok: false, message: 'Unexpected response from server.' }
}

export async function withdrawSellerSubmission(
  submissionId: string,
  body: { expectedCaseRevision: number; operationId: string },
): Promise<SellerApiResult<{ status: string; caseRevision: number | null }>> {
  const res = await fetchJson(
    `/api/seller/submissions/${encodeURIComponent(submissionId)}/withdraw`,
    {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  )
  const parsed = parseMaybeJson(await res.text())
  if (!res.ok) {
    const p = parseApiErrorBody(parsed)
    return { ok: false, message: p?.summary ?? `Could not withdraw submission (${res.status})` }
  }
  if (
    typeof parsed === 'object' &&
    parsed !== null &&
    'status' in parsed &&
    typeof (parsed as { status: unknown }).status === 'string'
  ) {
    const raw = parsed as { status: string; caseRevision?: unknown }
    return {
      ok: true,
      value: {
        status: raw.status,
        caseRevision: typeof raw.caseRevision === 'number' ? raw.caseRevision : null,
      },
    }
  }
  return { ok: false, message: 'Unexpected response from server.' }
}
