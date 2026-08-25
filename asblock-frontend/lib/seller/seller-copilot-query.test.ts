import { afterEach, describe, expect, it, vi } from 'vitest'
import { enqueueListingCopilot } from '@/lib/seller/seller-copilot-api'
import { sellerCopilotKeys } from '@/lib/seller/seller-copilot-query'

describe('seller-copilot-api', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('forwards AbortSignal on enqueue', async () => {
    const controller = new AbortController()
    let seen: AbortSignal | undefined
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        seen = init?.signal ?? undefined
        return new Response(
          JSON.stringify({
            jobId: '11111111-1111-4111-8111-111111111111',
            assetVersionId: '22222222-2222-4222-8222-222222222222',
          }),
          { status: 202, headers: { 'Content-Type': 'application/json' } },
        )
      }),
    )

    await enqueueListingCopilot('22222222-2222-4222-8222-222222222222', controller.signal)
    expect(seen).toBe(controller.signal)
  })
})

describe('sellerCopilotKeys', () => {
  it('scopes suggestion queries by version', () => {
    expect(sellerCopilotKeys.version('abc')).toEqual(['seller', 'copilot', 'version', 'abc'])
  })
})
