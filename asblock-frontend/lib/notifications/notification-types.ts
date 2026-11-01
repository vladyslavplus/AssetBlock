import { z } from 'zod'

export const notificationKindSchema = z.enum([
  'DOWNLOAD_READY',
  'ASSET_SOLD',
  'REVIEW_RECEIVED',
  'ORDER_READY',
  'ASSET_PROCESSING_READY',
  'ASSET_PROCESSING_REJECTED',
  'ASSET_PROCESSING_FAILED',
  'CODE_ANALYSIS_COMPLETED',
  'MODERATION_SUBMITTED',
  'MODERATION_REVIEW_STARTED',
  'MODERATION_CHANGES_REQUESTED',
  'MODERATION_APPROVED',
  'MODERATION_REJECTED',
  'MODERATION_WITHDRAWN',
  'MODERATION_APPEAL_UPHELD',
])

export type NotificationKind = z.infer<typeof notificationKindSchema>

export const moderationCaseChangedMessageSchema = z
  .object({
    submissionId: z.guid(),
    assetId: z.guid(),
    versionId: z.guid(),
    state: z.enum([
      'SUBMITTED',
      'IN_REVIEW',
      'CHANGES_REQUESTED',
      'APPROVED',
      'REJECTED',
      'WITHDRAWN',
    ]),
    revision: z.number().int().positive(),
    isAppeal: z.boolean(),
    decisionId: z.guid().optional(),
  })
  .strict()

export interface NotificationListItem {
  id: string
  kind: string
  metadataJson: string
  createdAt: string
  readAt: string | null
}

export interface PagedNotificationsDto {
  items: NotificationListItem[]
  totalCount: number
  page: number
  pageSize: number
  totalPages?: number
}
