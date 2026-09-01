import { parseUuidParam } from '@/lib/server/bff-params'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function DELETE(
  request: Request,
  context: { params: Promise<{ id: string; tagId: string }> },
) {
  const { id, tagId } = await context.params
  const parsedId = parseUuidParam('id', id)
  if (!parsedId.ok) {
    return parsedId.response
  }
  const parsedTagId = parseUuidParam('tagId', tagId)
  if (!parsedTagId.ok) {
    return parsedTagId.response
  }

  return proxyAuthenticatedBff(request, {
    path: `/api/assets/${encodeURIComponent(parsedId.value)}/tags/${encodeURIComponent(parsedTagId.value)}`,
    init: { method: 'DELETE' },
    enforceSameOrigin: true,
  })
}
