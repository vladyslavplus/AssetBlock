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

export const notificationListItemSchema = z
  .object({
    id: z.guid(),
    kind: z.string().min(1).max(100),
    metadataJson: z.string().max(16_384),
    createdAt: z.iso.datetime({ offset: true }),
    readAt: z.iso.datetime({ offset: true }).nullable(),
  })
  .strict()
export const pagedNotificationsSchema = z
  .object({
    items: z.array(notificationListItemSchema).max(100),
    totalCount: z.number().int().nonnegative(),
    page: z.number().int().positive(),
    pageSize: z.number().int().positive().max(100),
    totalPages: z.number().int().nonnegative().optional(),
  })
  .strict()

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

export type NotificationListItem = z.infer<typeof notificationListItemSchema>
export type PagedNotificationsDto = z.infer<typeof pagedNotificationsSchema>
