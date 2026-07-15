import { getApiErrorMessage } from '@/lib/http/api-errors'
import type { AccountProfile } from '@/lib/account/account-types'
import {
  parseSocialPlatformsResponse,
  type SocialPlatformOption,
} from '@/lib/account/social-links-account'

export const accountKeys = {
  all: ['account'] as const,
  me: () => [...accountKeys.all, 'me'] as const,
  socialPlatforms: () => [...accountKeys.all, 'socialPlatforms'] as const,
}

export async function fetchAccountProfile(): Promise<AccountProfile> {
  const res = await fetch('/api/account/me', { credentials: 'include' })
  const json: unknown = await res.json().catch(() => null)
  if (res.status === 401) {
    const err = new Error('UNAUTHORIZED')
    ;(err as Error & { status?: number }).status = 401
    throw err
  }
  if (!res.ok) {
    throw new Error(getApiErrorMessage(json, 'Could not load profile.'))
  }
  return json as AccountProfile
}

export async function fetchAccountSocialPlatforms(): Promise<SocialPlatformOption[]> {
  const res = await fetch('/api/account/social-platforms', { credentials: 'include' })
  const json: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new Error(getApiErrorMessage(json, 'Could not load social platforms.'))
  }
  return parseSocialPlatformsResponse(json)
}
