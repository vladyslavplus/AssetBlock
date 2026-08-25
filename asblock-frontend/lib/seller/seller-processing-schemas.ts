import { z } from 'zod'

export const assetProcessingJobTypeSchema = z.enum([
  'ARCHIVE_INSPECTION',
  'MALWARE_SCAN',
  'LISTING_COPILOT',
])
export type AssetProcessingJobType = z.infer<typeof assetProcessingJobTypeSchema>

export const assetProcessingJobStatusSchema = z.enum([
  'QUEUED',
  'RUNNING',
  'RETRY_SCHEDULED',
  'SUCCEEDED',
  'FAILED',
  'CANCELLED',
])
export type AssetProcessingJobStatus = z.infer<typeof assetProcessingJobStatusSchema>

export const TERMINAL_JOB_STATUSES = ['SUCCEEDED', 'FAILED', 'CANCELLED'] as const
export const NON_TERMINAL_JOB_STATUSES = ['QUEUED', 'RUNNING', 'RETRY_SCHEDULED'] as const

export function isTerminalStatus(status: AssetProcessingJobStatus): boolean {
  return status === 'SUCCEEDED' || status === 'FAILED' || status === 'CANCELLED'
}

export function isNonTerminalStatus(status: AssetProcessingJobStatus): boolean {
  return status === 'QUEUED' || status === 'RUNNING' || status === 'RETRY_SCHEDULED'
}

export const isoDateTimeSchema = z.string().datetime({ offset: true })

export const assetProcessingJobSchema = z
  .object({
    id: z.string().uuid(),
    assetId: z.string().uuid(),
    assetVersionId: z.string().uuid(),
    type: assetProcessingJobTypeSchema,
    definitionVersion: z.number().int(),
    status: assetProcessingJobStatusSchema,
    stage: z.string().min(1).max(64),
    attemptCount: z.number().int().nonnegative(),
    maxAttempts: z.number().int().positive(),
    availableAt: isoDateTimeSchema,
    startedAt: isoDateTimeSchema.nullable().optional(),
    completedAt: isoDateTimeSchema.nullable().optional(),
    errorCode: z.string().min(1).max(64).nullable().optional(),
    errorSummary: z.string().max(4000).nullable().optional(),
    createdAt: isoDateTimeSchema,
    updatedAt: isoDateTimeSchema.nullable().optional(),
  })
  .strict()

export type AssetProcessingJobDto = z.infer<typeof assetProcessingJobSchema>

export const assetProcessingUpdateMessageSchema = z
  .object({
    jobId: z.string().uuid(),
    assetId: z.string().uuid(),
    assetVersionId: z.string().uuid(),
    type: assetProcessingJobTypeSchema,
    status: assetProcessingJobStatusSchema,
    stage: z.string().min(1).max(64),
    updatedAt: isoDateTimeSchema.nullable().optional(),
  })
  .strict()

export type AssetProcessingUpdateMessage = z.infer<typeof assetProcessingUpdateMessageSchema>
