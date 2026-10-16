import { parseUuidParam } from '@/lib/server/bff-params'
import { problemResponse } from '@/lib/server/bff-http'
import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function GET(
  request: Request,
  context: { params: Promise<{ id: string; versionId: string; reportId: string }> },
) {
  const { id, versionId, reportId } = await context.params
  const asset = parseUuidParam('id', id)
  if (!asset.ok) return asset.response
  const version = parseUuidParam('versionId', versionId)
  if (!version.ok) return version.response
  const report = parseUuidParam('reportId', reportId)
  if (!report.ok) return report.response

  const rawPage = new URL(request.url).searchParams.get('page') ?? '1'
  if (!/^[1-9]\d{0,5}$/.test(rawPage) || Number(rawPage) > 100000) {
    return problemResponse(400, 'ERR_VALIDATION_FAILED', 'Page must be a positive integer.')
  }
  return proxyAuthenticatedBff(request, {
    path: `/api/users/me/asset-versions/${encodeURIComponent(version.value)}/analysis/reports/${encodeURIComponent(report.value)}/findings?page=${rawPage}`,
    init: { method: 'GET' },
  })
}
