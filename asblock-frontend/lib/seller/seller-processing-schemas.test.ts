import { describe, expect, it } from 'vitest'
import {
  assetProcessingJobSchema,
  assetProcessingUpdateMessageSchema,
  isNonTerminalStatus,
  isTerminalStatus,
} from '@/lib/seller/seller-processing-schemas'

describe('seller-processing-schemas', () => {
  const validJob = {
    id: '11111111-1111-4111-8111-111111111111',
    assetId: '22222222-2222-4222-8222-222222222222',
    assetVersionId: '33333333-3333-4333-8333-333333333333',
    type: 'ARCHIVE_INSPECTION',
    definitionVersion: 1,
    status: 'RUNNING',
    stage: 'INSPECTING',
    attemptCount: 1,
    maxAttempts: 3,
    availableAt: '2026-08-24T12:00:00Z',
    startedAt: '2026-08-24T12:00:01Z',
    completedAt: null,
    errorCode: null,
    errorSummary: null,
    createdAt: '2026-08-24T12:00:00Z',
    updatedAt: '2026-08-24T12:00:01Z',
  }

  it('accepts valid AssetProcessingJobDto contract', () => {
    const parsed = assetProcessingJobSchema.parse(validJob)
    expect(parsed.id).toBe(validJob.id)
    expect(parsed.type).toBe('ARCHIVE_INSPECTION')
    expect(parsed.status).toBe('RUNNING')
    expect(parsed.stage).toBe('INSPECTING')
  })

  it('rejects internal or unexpected fields in AssetProcessingJobDto', () => {
    const withPayload = {
      ...validJob,
      payload: '{"some":"secret"}',
    }
    expect(() => assetProcessingJobSchema.parse(withPayload)).toThrow()

    const withLease = {
      ...validJob,
      leaseToken: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    }
    expect(() => assetProcessingJobSchema.parse(withLease)).toThrow()

    const withResult = {
      ...validJob,
      result: '{"fileCount":10}',
    }
    expect(() => assetProcessingJobSchema.parse(withResult)).toThrow()
  })

  it('accepts valid AssetProcessingUpdateMessage contract', () => {
    const validUpdate = {
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'MALWARE_SCAN',
      status: 'SUCCEEDED',
      stage: 'CLEAN',
      updatedAt: '2026-08-24T12:00:05Z',
    }
    const parsed = assetProcessingUpdateMessageSchema.parse(validUpdate)
    expect(parsed.jobId).toBe(validUpdate.jobId)
    expect(parsed.status).toBe('SUCCEEDED')
  })

  it('rejects malformed SignalR update message', () => {
    const malformed = {
      jobId: 'not-a-uuid',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'UNKNOWN_TYPE',
      status: 'UNKNOWN_STATUS',
      stage: '',
    }
    expect(() => assetProcessingUpdateMessageSchema.parse(malformed)).toThrow()
  })

  it('correctly classifies terminal and non-terminal statuses', () => {
    expect(isTerminalStatus('SUCCEEDED')).toBe(true)
    expect(isTerminalStatus('FAILED')).toBe(true)
    expect(isTerminalStatus('CANCELLED')).toBe(true)
    expect(isTerminalStatus('QUEUED')).toBe(false)
    expect(isTerminalStatus('RUNNING')).toBe(false)
    expect(isTerminalStatus('RETRY_SCHEDULED')).toBe(false)

    expect(isNonTerminalStatus('QUEUED')).toBe(true)
    expect(isNonTerminalStatus('RUNNING')).toBe(true)
    expect(isNonTerminalStatus('RETRY_SCHEDULED')).toBe(true)
    expect(isNonTerminalStatus('SUCCEEDED')).toBe(false)
    expect(isNonTerminalStatus('FAILED')).toBe(false)
    expect(isNonTerminalStatus('CANCELLED')).toBe(false)
  })

  it('accepts errorSummary up to 4000 characters and rejects excessive length', () => {
    const summary501 = 'a'.repeat(501)
    const summary2000 = 'b'.repeat(2000)
    const summary4000 = 'c'.repeat(4000)
    const summaryExcessive = 'd'.repeat(4001)

    const job501 = { ...validJob, errorSummary: summary501 }
    expect(assetProcessingJobSchema.parse(job501).errorSummary).toBe(summary501)

    const job2000 = { ...validJob, errorSummary: summary2000 }
    expect(assetProcessingJobSchema.parse(job2000).errorSummary).toBe(summary2000)

    const job4000 = { ...validJob, errorSummary: summary4000 }
    expect(assetProcessingJobSchema.parse(job4000).errorSummary).toBe(summary4000)

    const jobExcessive = { ...validJob, errorSummary: summaryExcessive }
    expect(() => assetProcessingJobSchema.parse(jobExcessive)).toThrow()
  })

  it('accepts valid ISO datetime timestamps with UTC Z and timezone offsets, and rejects invalid strings', () => {
    const jobWithUtc = { ...validJob, availableAt: '2026-08-24T12:00:00Z' }
    expect(assetProcessingJobSchema.parse(jobWithUtc).availableAt).toBe('2026-08-24T12:00:00Z')

    const jobWithOffset = { ...validJob, availableAt: '2026-08-24T14:00:00+02:00' }
    expect(assetProcessingJobSchema.parse(jobWithOffset).availableAt).toBe(
      '2026-08-24T14:00:00+02:00',
    )

    const jobWithNegativeOffset = { ...validJob, availableAt: '2026-08-24T07:00:00-05:00' }
    expect(assetProcessingJobSchema.parse(jobWithNegativeOffset).availableAt).toBe(
      '2026-08-24T07:00:00-05:00',
    )

    const jobWithInvalidDate = { ...validJob, availableAt: 'not-a-date' }
    expect(() => assetProcessingJobSchema.parse(jobWithInvalidDate)).toThrow()

    const jobWithInvalidFormat = { ...validJob, createdAt: '2026-08-24' } // lacks time / offset
    expect(() => assetProcessingJobSchema.parse(jobWithInvalidFormat)).toThrow()
  })
})
