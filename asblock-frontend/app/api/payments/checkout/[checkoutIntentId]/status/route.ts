import { cookies } from 'next/headers'
import { fetchBackendAuthorized } from '@/lib/server/backend-authorized'
import { forwardBackendResponse } from '@/lib/server/bff-http'

interface RouteContext {
  params: Promise<{ checkoutIntentId: string }>
}

export async function GET(_request: Request, context: RouteContext) {
  const { checkoutIntentId } = await context.params
  if (!checkoutIntentId?.trim()) {
    return Response.json({ title: 'Not Found', status: 404 }, { status: 404 })
  }

  const store = await cookies()
  const res = await fetchBackendAuthorized(
    store,
    `/api/payments/checkout/${encodeURIComponent(checkoutIntentId)}/status`,
    { method: 'GET' },
  )
  return forwardBackendResponse(res)
}
