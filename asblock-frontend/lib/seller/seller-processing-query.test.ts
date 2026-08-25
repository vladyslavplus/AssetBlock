import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  fetchAssetProcessingJobs,
  fetchAssetVersionProcessingJobs,
} from '@/lib/seller/seller-processing-api'
import {
  PROCESSING_POLL_INTERVAL_MS,
  sellerProcessingKeys,
} from '@/lib/seller/seller-processing-query'
import {
  isNonTerminalStatus,
  type AssetProcessingJobDto,
} from '@/lib/seller/seller-processing-schemas'

describe('seller-processing-query', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const sampleJob: AssetProcessingJobDto = {
    id: '11111111-1111-4111-8111-111111111111',
    assetId: '22222222-2222-4222-8222-222222222222',
    assetVersionId: '33333333-3333-4333-8333-333333333333',
    type: 'ARCHIVE_INSPECTION',
    definitionVersion: 1,
    status: 'RUNNING',
    stage: 'RUNNING',
    attemptCount: 1,
    maxAttempts: 3,
    availableAt: '2026-08-24T12:00:00Z',
    startedAt: '2026-08-24T12:00:01Z',
    completedAt: null,
    errorCode: null,
    errorSummary: null,
    createdAt: '2026-08-24T12:00:00Z',
    updatedAt: null,
  }

  it('fetchAssetProcessingJobs forwards AbortSignal', async () => {
    const controller = new AbortController()
    let seenSignal: AbortSignal | undefined

    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        seenSignal = init?.signal ?? undefined
        return new Response(JSON.stringify([sampleJob]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )

    const jobs = await fetchAssetProcessingJobs(sampleJob.assetId, controller.signal)
    expect(seenSignal).toBe(controller.signal)
    expect(jobs).toHaveLength(1)
    expect(jobs[0].id).toBe(sampleJob.id)
  })

  it('fetchAssetVersionProcessingJobs forwards AbortSignal', async () => {
    const controller = new AbortController()
    let seenSignal: AbortSignal | undefined

    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        seenSignal = init?.signal ?? undefined
        return new Response(JSON.stringify([sampleJob]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )

    const jobs = await fetchAssetVersionProcessingJobs(sampleJob.assetVersionId, controller.signal)
    expect(seenSignal).toBe(controller.signal)
    expect(jobs).toHaveLength(1)
    expect(jobs[0].assetVersionId).toBe(sampleJob.assetVersionId)
  })

  it('evaluates polling logic: continues for active and stops for terminal/empty', () => {
    const activeJobs = [
      { ...sampleJob, status: 'SUCCEEDED' as const },
      { ...sampleJob, id: '22222222-2222-4222-8222-222222222222', status: 'RUNNING' as const },
    ]

    const allTerminalJobs = [
      { ...sampleJob, status: 'SUCCEEDED' as const },
      { ...sampleJob, id: '22222222-2222-4222-8222-222222222222', status: 'FAILED' as const },
    ]

    const emptyJobs: AssetProcessingJobDto[] = []

    const checkPoll = (jobs: AssetProcessingJobDto[] | undefined) => {
      if (!jobs || jobs.length === 0) return false
      const hasActive = jobs.some((j) => isNonTerminalStatus(j.status))
      return hasActive ? PROCESSING_POLL_INTERVAL_MS : false
    }

    expect(checkPoll(activeJobs)).toBe(5000)
    expect(checkPoll(allTerminalJobs)).toBe(false)
    expect(checkPoll(emptyJobs)).toBe(false)
    expect(checkPoll(undefined)).toBe(false)
  })

  it('constructs correct query keys', () => {
    expect(sellerProcessingKeys.all).toEqual(['seller', 'processing'])
    expect(sellerProcessingKeys.asset('asset-123')).toEqual([
      'seller',
      'processing',
      'asset',
      'asset-123',
    ])
    expect(sellerProcessingKeys.version('version-456')).toEqual([
      'seller',
      'processing',
      'version',
      'version-456',
    ])
  })
})
