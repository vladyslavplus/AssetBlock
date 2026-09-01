import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  fetchAssetProcessingJobs,
  fetchAssetVersionProcessingJobs,
} from '@/lib/seller/seller-processing-api'
import {
  resolveProcessingPollInterval,
  sellerProcessingKeys,
} from '@/lib/seller/seller-processing-query'
import type { AssetProcessingJobDto } from '@/lib/seller/seller-processing-schemas'

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

  it('evaluates polling logic: active jobs poll on disconnected/connecting/reconnecting and pause on connected', () => {
    const activeJobs = [
      { ...sampleJob, status: 'SUCCEEDED' as const },
      { ...sampleJob, id: '22222222-2222-4222-8222-222222222222', status: 'RUNNING' as const },
    ]

    const allTerminalJobs = [
      { ...sampleJob, status: 'SUCCEEDED' as const },
      { ...sampleJob, id: '22222222-2222-4222-8222-222222222222', status: 'FAILED' as const },
    ]

    const emptyJobs: AssetProcessingJobDto[] = []

    // When connected, active jobs pause HTTP polling
    expect(resolveProcessingPollInterval(activeJobs, 'connected')).toBe(false)

    // When disconnected, connecting, or reconnecting, active jobs poll every 5s
    expect(resolveProcessingPollInterval(activeJobs, 'disconnected')).toBe(5000)
    expect(resolveProcessingPollInterval(activeJobs, 'connecting')).toBe(5000)
    expect(resolveProcessingPollInterval(activeJobs, 'reconnecting')).toBe(5000)

    // Terminal or empty jobs never poll regardless of connection state
    expect(resolveProcessingPollInterval(allTerminalJobs, 'disconnected')).toBe(false)
    expect(resolveProcessingPollInterval(allTerminalJobs, 'connected')).toBe(false)
    expect(resolveProcessingPollInterval(emptyJobs, 'disconnected')).toBe(false)
    expect(resolveProcessingPollInterval(emptyJobs, 'connected')).toBe(false)
    expect(resolveProcessingPollInterval(undefined, 'disconnected')).toBe(false)
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
