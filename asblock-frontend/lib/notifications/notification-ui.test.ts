import { describe, expect, it } from 'vitest'

import {
  formatHubToastMessage,
  getNotificationHref,
  getNotificationTitle,
} from '@/lib/notifications/notification-ui'
import {
  notificationKindSchema,
  pagedNotificationsSchema,
} from '@/lib/notifications/notification-types'

describe('notification-ui processing kinds', () => {
  const metadata = JSON.stringify({
    notificationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
    assetVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    processingStatus: 'READY',
    assetTitle: 'Forest Pack',
  })

  it('validates strict paged notification responses while allowing unknown string kinds', () => {
    const valid = {
      items: [
        {
          id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
          kind: 'UNKNOWN_FUTURE_KIND',
          metadataJson: '{}',
          createdAt: '2026-01-02T00:00:00.000Z',
          readAt: null,
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 4,
      totalPages: 1,
    }
    expect(pagedNotificationsSchema.safeParse(valid).success).toBe(true)
    expect(pagedNotificationsSchema.safeParse({ ...valid, unexpected: true }).success).toBe(false)
  })

  it('maps durable processing hub methods to titles and owner manage links', () => {
    expect(getNotificationTitle('AssetProcessingReady')).toBe('Listing ready')
    expect(getNotificationTitle('ASSET_PROCESSING_REJECTED')).toBe('Listing rejected')
    expect(getNotificationTitle('AssetProcessingFailed')).toBe('Listing processing failed')
    expect(getNotificationHref('AssetProcessingReady', metadata)).toBe(
      '/sell/assets/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/edit',
    )
    expect(formatHubToastMessage('AssetProcessingReady', JSON.parse(metadata))).toBe(
      'Listing ready: Forest Pack',
    )
  })

  it('provides specific titles for every backend notification kind and safe fallback for unknown kinds', () => {
    for (const kind of notificationKindSchema.options) {
      expect(getNotificationTitle(kind)).not.toBe('Notification')
    }
    expect(getNotificationTitle('unknown_kind')).toBe('Notification')
    expect(getNotificationTitle('MODERATION_APPEAL_UPHELD')).toBe('Appeal upheld')
  })

  it('routes owner outcomes to seller review even when owner is a Moderator', () => {
    const moderationMetadata = JSON.stringify({
      submissionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      versionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    })
    for (const kind of [
      'MODERATION_REVIEW_STARTED',
      'MODERATION_CHANGES_REQUESTED',
      'MODERATION_APPROVED',
      'MODERATION_REJECTED',
      'MODERATION_APPEAL_UPHELD',
    ]) {
      expect(getNotificationHref(kind, moderationMetadata, 'Moderator')).toBe(
        '/sell/assets/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/edit',
      )
    }
    expect(getNotificationHref('MODERATION_SUBMITTED', moderationMetadata, 'Moderator')).toBe(
      '/moderation',
    )
    expect(getNotificationHref('MODERATION_WITHDRAWN', moderationMetadata, 'Moderator')).toBe(
      '/moderation',
    )
    expect(getNotificationHref('MODERATION_SUBMITTED', moderationMetadata, 'User')).toBe(
      '/sell/assets/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/edit',
    )
    for (const kind of notificationKindSchema.options.filter((value) =>
      value.startsWith('MODERATION_'),
    )) {
      expect(getNotificationHref(kind, moderationMetadata, 'User')).toBe(
        '/sell/assets/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/edit',
      )
    }
  })
})
