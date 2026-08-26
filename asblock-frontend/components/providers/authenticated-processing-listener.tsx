'use client'

import { useAuth } from '@/components/auth/auth-context'
import { useAssetProcessingSubscription } from '@/hooks/use-asset-processing-subscription'

export function AuthenticatedProcessingListener() {
  const { status, user } = useAuth()
  useAssetProcessingSubscription(status === 'authenticated', user?.id)
  return null
}
