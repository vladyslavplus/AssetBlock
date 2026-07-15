import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET() {
  const base = getServerApiBaseUrl()
  const res = await fetch(`${base}/api/users/social-platforms`, { cache: 'no-store' })
  return forwardBackendResponse(res)
}
