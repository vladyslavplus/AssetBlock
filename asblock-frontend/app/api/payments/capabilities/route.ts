import { getServerApiBaseUrl } from '@/lib/http/api-config'
import { forwardBackendResponse } from '@/lib/server/bff-http'

export async function GET() {
  const base = getServerApiBaseUrl()
  const res = await fetch(`${base}/api/payments/capabilities`, { cache: 'no-store' })
  return forwardBackendResponse(res)
}
