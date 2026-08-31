import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardAuthenticatedBackendResponse } from '@/lib/server/bff-http'
import { parseUuidParam } from '@/lib/server/bff-params'

interface RouteContext {
  params: Promise<{ checkoutIntentId: string }>
}

export async function GET(request: Request, context: RouteContext) {
  const { checkoutIntentId } = await context.params
  const parsedId = parseUuidParam('checkoutIntentId', checkoutIntentId)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/payments/checkout/${encodeURIComponent(parsedId.value)}/status`,
    { method: 'GET', signal: request.signal },
  )
  return forwardAuthenticatedBackendResponse(res)
}
