import { forwardBackendResponse } from '@/lib/server/bff-http'
import { fetchBackendPublic } from '@/lib/server/fetch-backend'

export async function GET(request: Request) {
  const res = await fetchBackendPublic('/api/payments/capabilities', {
    method: 'GET',
    signal: request.signal,
  })
  return forwardBackendResponse(res)
}
