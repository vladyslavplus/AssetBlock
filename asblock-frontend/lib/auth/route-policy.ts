import { APP_ROLE_ADMIN, type AppRole } from '@/lib/auth/roles'

export interface RoutePolicy {
  prefix: string
  sessionRequired: boolean
  role?: AppRole
}

export const ROUTE_POLICIES: readonly RoutePolicy[] = [
  { prefix: '/admin', sessionRequired: true, role: APP_ROLE_ADMIN },
  { prefix: '/library', sessionRequired: true },
  { prefix: '/account', sessionRequired: true },
  { prefix: '/sell', sessionRequired: true },
]

export function getRoutePolicy(pathname: string): RoutePolicy | undefined {
  return ROUTE_POLICIES.find(
    ({ prefix }) => pathname === prefix || pathname.startsWith(`${prefix}/`),
  )
}
