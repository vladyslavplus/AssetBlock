import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function GET(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/users/me/asset-versions/${encodeURIComponent(parsedId.value)}/listing-copilot`,
    init: { method: 'GET' },
  })
}

export async function POST(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/users/me/asset-versions/${encodeURIComponent(parsedId.value)}/listing-copilot`,
    init: { method: 'POST' },
    enforceSameOrigin: true,
  })
}
