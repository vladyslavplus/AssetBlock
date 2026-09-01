import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function PATCH(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  const path = `/api/users/me/notifications/${encodeURIComponent(parsedId.value)}/read`
  return proxyAuthenticatedBff(request, {
    path,
    init: { method: 'PATCH' },
    enforceSameOrigin: true,
  })
}
