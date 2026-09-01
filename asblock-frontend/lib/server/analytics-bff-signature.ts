import 'server-only'

import { createHmac } from 'node:crypto'
import { getServerEnvironment } from '@/lib/env'

/** Must match backend AnalyticsBffRateLimitHeaders. */
export const ANALYTICS_BFF_HEADER_PARTITION = 'X-AssetBlock-Analytics-Partition'
export const ANALYTICS_BFF_HEADER_TIMESTAMP = 'X-AssetBlock-Analytics-Timestamp'
export const ANALYTICS_BFF_HEADER_SIGNATURE = 'X-AssetBlock-Analytics-Signature'

const PARTITION_PREFIX = 'assetblock:analytics:partition:v1\n'
const REQUEST_PREFIX = 'assetblock:analytics:request:v1\nPOST\n/api/analytics/events\n'

/**
 * Cross-runtime golden vector (must match AssetBlock.WebApi.Tests AnalyticsBffSignatureValidatorTests):
 * secret = golden_vector_analytics_bff_signing_secret_v1
 * ip = 203.0.113.10
 * timestamp = 1700000000
 * partition = a862646e90e49ba9447d3f44225e5cb2acf8252f4f74bf9333b66cd3ab56b22a
 * signature = 9131820c941bc95d780d35ebbe4b0de9374d331d361edd693b03a849d0155467
 */

let signingSecretMissingLogged = false

export interface AnalyticsBffRateLimitHeaders {
  partition: string
  timestamp: string
  signature: string
}

function resolveSigningSecret(): string | null {
  const secret = getServerEnvironment().analyticsBffSigningSecret
  if (!secret) {
    if (!signingSecretMissingLogged) {
      signingSecretMissingLogged = true
      console.warn(
        '[analytics-bff-signature] ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET is missing; analytics forwarding is disabled.',
      )
    }
    return null
  }
  return secret
}

function hmacHex(secret: string, payload: string): string {
  return createHmac('sha256', secret).update(payload, 'utf8').digest('hex')
}

export function createAnalyticsBffPartition(normalizedIp: string, secret: string): string {
  return hmacHex(secret, `${PARTITION_PREFIX}${normalizedIp}`)
}

export function createAnalyticsBffRequestSignature(
  timestamp: string,
  partition: string,
  secret: string,
): string {
  return hmacHex(secret, `${REQUEST_PREFIX}${timestamp}\n${partition}`)
}

export function createAnalyticsBffRateLimitHeaders(
  normalizedIp: string,
): AnalyticsBffRateLimitHeaders | null {
  const secret = resolveSigningSecret()
  if (!secret) {
    return null
  }

  const partition = createAnalyticsBffPartition(normalizedIp, secret)
  const timestamp = String(Math.floor(Date.now() / 1000))
  const signature = createAnalyticsBffRequestSignature(timestamp, partition, secret)

  return { partition, timestamp, signature }
}
