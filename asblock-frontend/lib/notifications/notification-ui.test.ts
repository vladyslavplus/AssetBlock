import { describe, expect, it } from 'vitest'

import {
  formatHubToastMessage,
  getNotificationHref,
  getNotificationTitle,
} from '@/lib/notifications/notification-ui'

describe('notification-ui processing kinds', () => {
  const metadata = JSON.stringify({
    notificationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
    assetVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    processingStatus: 'READY',
    assetTitle: 'Forest Pack',
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
})
