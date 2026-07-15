import { ApiRequestError } from '@/lib/http/api-client'
import { getApiErrorMessage, readApiResponseBody } from '@/lib/http/api-errors'

async function readResponse(res: Response): Promise<unknown> {
  const body = await readApiResponseBody(res)
  if (!res.ok) {
    throw new ApiRequestError(
      getApiErrorMessage(body, `Request failed (${res.status})`),
      res.status,
      body,
    )
  }
  return body
}

export async function adminPostJson(path: string, jsonBody: unknown): Promise<unknown> {
  const res = await fetch(path, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(jsonBody),
  })
  return readResponse(res)
}

export async function adminPutJson(path: string, jsonBody: unknown): Promise<unknown> {
  const res = await fetch(path, {
    method: 'PUT',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(jsonBody),
  })
  return readResponse(res)
}

export async function adminDelete(path: string): Promise<void> {
  const res = await fetch(path, { method: 'DELETE', credentials: 'include' })
  await readResponse(res)
}
