import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

interface RouteContext {
  params: Promise<{ checkoutIntentId: string }>
}

export async function GET(request: Request, context: RouteContext) {
  const { checkoutIntentId } = await context.params
  const parsedId = parseUuidParam('checkoutIntentId', checkoutIntentId)
  if (!parsedId.ok) {
    return parsedId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/payments/checkout/${encodeURIComponent(parsedId.value)}/status`,
    init: { method: 'GET' },
  })
}
