import { z } from 'zod'

const optionalTrimmedString = z.preprocess(
  (value) => (typeof value === 'string' && value.trim() === '' ? undefined : value),
  z.string().trim().optional(),
)

const httpUrlSchema = z
  .string()
  .trim()
  .url()
  .refine((value) => {
    const protocol = new URL(value).protocol
    return protocol === 'http:' || protocol === 'https:'
  }, 'Must be an HTTP(S) URL')
  .transform((value) => value.replace(/\/+$/, ''))

const publicEnvironmentSchema = z.object({
  NEXT_PUBLIC_API_BASE_URL: httpUrlSchema,
})

const serverEnvironmentSchema = z.object({
  ASSETBLOCK_API_BASE_URL: httpUrlSchema,
  ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET: z.preprocess(
    (value) => (typeof value === 'string' && value.trim() === '' ? undefined : value),
    z.string().trim().min(32).optional(),
  ),
  TRUSTED_CLIENT_IP_HEADER: optionalTrimmedString.refine(
    (value) => value === undefined || /^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/.test(value),
    'Must be a valid HTTP header name',
  ),
})

export interface AssetBlockEnvironment {
  publicApiBaseUrl: string
  serverApiBaseUrl: string
  analyticsBffSigningSecret?: string
  trustedClientIpHeader?: string
}

export type PublicEnvironment = Pick<AssetBlockEnvironment, 'publicApiBaseUrl'>
export type ServerEnvironment = Omit<AssetBlockEnvironment, 'publicApiBaseUrl'>

interface PublicEnvironmentSource {
  NEXT_PUBLIC_API_BASE_URL?: string
}

interface ServerEnvironmentSource {
  ASSETBLOCK_API_BASE_URL?: string
  ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET?: string
  TRUSTED_CLIENT_IP_HEADER?: string
}

export function getPublicEnvironment(
  environment: PublicEnvironmentSource = {
    NEXT_PUBLIC_API_BASE_URL: process.env.NEXT_PUBLIC_API_BASE_URL,
  },
): PublicEnvironment {
  const parsed = publicEnvironmentSchema.parse(environment)
  return { publicApiBaseUrl: parsed.NEXT_PUBLIC_API_BASE_URL }
}

export function getServerEnvironment(
  environment: ServerEnvironmentSource = {
    ASSETBLOCK_API_BASE_URL: process.env.ASSETBLOCK_API_BASE_URL,
    ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET: process.env.ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET,
    TRUSTED_CLIENT_IP_HEADER: process.env.TRUSTED_CLIENT_IP_HEADER,
  },
): ServerEnvironment {
  const parsed = serverEnvironmentSchema.parse(environment)
  return {
    serverApiBaseUrl: parsed.ASSETBLOCK_API_BASE_URL,
    analyticsBffSigningSecret: parsed.ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET,
    trustedClientIpHeader: parsed.TRUSTED_CLIENT_IP_HEADER?.toLowerCase(),
  }
}

export function parseEnvironment(
  environment: PublicEnvironmentSource & ServerEnvironmentSource,
): AssetBlockEnvironment {
  return { ...getPublicEnvironment(environment), ...getServerEnvironment(environment) }
}

/** Eager validation entry point for the root layout. */
export function validateEnvironment(): void {
  parseEnvironment({
    NEXT_PUBLIC_API_BASE_URL: process.env.NEXT_PUBLIC_API_BASE_URL,
    ASSETBLOCK_API_BASE_URL: process.env.ASSETBLOCK_API_BASE_URL,
    ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET: process.env.ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET,
    TRUSTED_CLIENT_IP_HEADER: process.env.TRUSTED_CLIENT_IP_HEADER,
  })
}
