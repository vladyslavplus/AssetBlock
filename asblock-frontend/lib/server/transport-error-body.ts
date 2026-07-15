import { describeServerFetchError } from '@/lib/server/fetch-errors'

export function transportErrorBody(error: unknown) {
  const code = 'ERR_API_TRANSPORT'
  return {
    type: `urn:assetblock:error:${code}`,
    status: 502,
    title: 'Bad Gateway',
    detail: describeServerFetchError(error),
    code,
    traceId: crypto.randomUUID(),
  }
}
