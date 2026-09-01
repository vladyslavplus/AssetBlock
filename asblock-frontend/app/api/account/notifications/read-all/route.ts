import { proxyAuthenticatedBff } from '@/lib/server/bff-route'

export async function POST(request: Request) {
  return proxyAuthenticatedBff(request, {
    path: '/api/users/me/notifications/read-all',
    init: { method: 'POST' },
    enforceSameOrigin: true,
  })
}
