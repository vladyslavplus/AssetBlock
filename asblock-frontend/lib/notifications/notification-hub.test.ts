import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { subscribeProcessingHub } from '@/lib/notifications/notification-hub'
import type { AssetProcessingUpdateMessage } from '@/lib/seller/seller-processing-schemas'

const handlers = vi.hoisted<Record<string, (payload: unknown) => void>>(() => ({}))

const mockHubConnection = vi.hoisted(() => ({
  on: vi.fn((event: string, cb: (payload: unknown) => void) => {
    handlers[event] = cb
  }),
  start: vi.fn(async () => {}),
  stop: vi.fn(async () => {}),
  state: 'Connected',
}))

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    configureLogging: vi.fn().mockReturnThis(),
    build: vi.fn(() => mockHubConnection),
  })),
  HttpTransportType: {
    WebSockets: 1,
    ServerSentEvents: 2,
    LongPolling: 4,
  },
  LogLevel: {
    Debug: 1,
    Information: 2,
    Warning: 3,
    Error: 4,
  },
  HubConnectionState: {
    Disconnected: 'Disconnected',
    Connecting: 'Connecting',
    Connected: 'Connected',
    Disconnecting: 'Disconnecting',
    Reconnecting: 'Reconnecting',
  },
}))

vi.mock('@/lib/notifications/notifications-hub-url', () => ({
  getNotificationsHubUrl: () => 'http://localhost:5000/hubs/notifications',
}))

vi.mock('sonner', () => ({
  toast: { info: vi.fn(), error: vi.fn(), success: vi.fn() },
}))

describe('notification-hub processing subscription', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ accessToken: 'test-token' }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('dispatches valid AssetProcessingUpdated event to subscriber', () => {
    const received: AssetProcessingUpdateMessage[] = []
    const unsubscribe = subscribeProcessingHub((msg) => {
      received.push(msg)
    })

    const onHandler = handlers.AssetProcessingUpdated
    expect(onHandler).toBeDefined()

    const validPayload = {
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'ARCHIVE_INSPECTION',
      status: 'RUNNING',
      stage: 'INSPECTING',
      updatedAt: '2026-08-24T12:00:00Z',
    }

    onHandler(validPayload)

    expect(received).toHaveLength(1)
    expect(received[0].jobId).toBe(validPayload.jobId)
    expect(received[0].status).toBe('RUNNING')

    unsubscribe()
  })

  it('safely ignores malformed AssetProcessingUpdated event without throwing or notifying subscriber', () => {
    const received: AssetProcessingUpdateMessage[] = []
    const unsubscribe = subscribeProcessingHub((msg) => {
      received.push(msg)
    })

    const onHandler = handlers.AssetProcessingUpdated
    expect(onHandler).toBeDefined()

    const malformedPayload = {
      jobId: 'invalid-id',
      type: 'UNKNOWN_TYPE',
    }

    expect(() => onHandler(malformedPayload)).not.toThrow()
    expect(received).toHaveLength(0)

    unsubscribe()
  })

  it('unsubscribing removes listener from subsequent dispatches', () => {
    const received: AssetProcessingUpdateMessage[] = []
    const unsubscribe = subscribeProcessingHub((msg) => {
      received.push(msg)
    })

    const onHandler = handlers.AssetProcessingUpdated

    const validPayload = {
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'MALWARE_SCAN',
      status: 'SUCCEEDED',
      stage: 'CLEAN',
      updatedAt: '2026-08-24T12:00:00Z',
    }

    unsubscribe()
    onHandler(validPayload)

    expect(received).toHaveLength(0)
  })
})
