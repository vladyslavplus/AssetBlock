import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

/** Entitled version history (author, purchaser, or public active listing). */
export async function GET(_request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/assets/${encodeURIComponent(id)}/versions`,
    { method: 'GET' },
    { persistRefreshedTokens: false },
  )
  return forwardBackendResponse(res)
}
