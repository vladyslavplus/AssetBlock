import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function DELETE(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/reviews/${encodeURIComponent(parsedId.value)}`,
    init: { method: 'DELETE' },
    enforceSameOrigin: true,
  })
}
