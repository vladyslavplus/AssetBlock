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
  recommendationPreferences: () => [...accountKeys.all, 'recommendationPreferences'] as const,
}

export interface RecommendationPreferences {
  isPersonalized: boolean
  optedInAt: string | null
}

export async function fetchRecommendationPreferences(): Promise<RecommendationPreferences> {
  const res = await fetch('/api/account/recommendation-preferences', { credentials: 'include' })
  const json: unknown = await res.json().catch(() => null)
  if (res.status === 401) {
    const err = new Error('UNAUTHORIZED')
    ;(err as Error & { status?: number }).status = 401
    throw err
  }
  if (!res.ok || typeof json !== 'object' || json === null) {
    throw new Error(getApiErrorMessage(json, 'Could not load recommendation preferences.'))
  }
  const { isPersonalized, optedInAt } = json as {
    isPersonalized?: unknown
    optedInAt?: unknown
  }
  if (typeof isPersonalized !== 'boolean') {
    throw new Error('Unexpected response from server.')
  }
  return {
    isPersonalized,
    optedInAt: typeof optedInAt === 'string' ? optedInAt : null,
  }
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
