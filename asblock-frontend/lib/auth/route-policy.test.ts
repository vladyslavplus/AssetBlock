import { getRoutePolicy, ROUTE_POLICIES } from '@/lib/auth/route-policy'
import { APP_ROLE_ADMIN, APP_ROLE_MODERATOR, isModeratorRole, isAdminRole } from '@/lib/auth/roles'
import { describe, expect, it } from 'vitest'

describe('route policy', () => {
  it('guards /moderation with the Moderator role', () => {
    const policy = getRoutePolicy('/moderation')
    expect(policy?.role).toBe(APP_ROLE_MODERATOR)
    expect(policy?.sessionRequired).toBe(true)
  })

  it('matches nested moderation paths', () => {
    expect(getRoutePolicy('/moderation/submissions')?.prefix).toBe('/moderation')
  })

  it('keeps /admin Admin-only', () => {
    expect(getRoutePolicy('/admin')?.role).toBe(APP_ROLE_ADMIN)
  })

  it('does not let Admin inherit the Moderator role in role checks', () => {
    expect(isModeratorRole(APP_ROLE_ADMIN)).toBe(false)
    expect(isModeratorRole(APP_ROLE_MODERATOR)).toBe(true)
    expect(isAdminRole(APP_ROLE_MODERATOR)).toBe(false)
  })

  it('lists both privileged areas as session-required policies', () => {
    const prefixes = ROUTE_POLICIES.map((policy) => policy.prefix)
    expect(prefixes).toContain('/moderation')
    expect(prefixes).toContain('/admin')
  })
})
