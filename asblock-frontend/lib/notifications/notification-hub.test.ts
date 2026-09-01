import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { toast } from 'sonner'
import {
  _resetNotificationHubForTest,
  subscribeNotificationHub,
  subscribeProcessingHub,
} from '@/lib/notifications/notification-hub'
import {
  getHubConnectionState,
  _resetHubConnectionStateForTest,
} from '@/lib/notifications/hub-connection-state'
import type { AssetProcessingUpdateMessage } from '@/lib/seller/seller-processing-schemas'

const handlers = vi.hoisted<Record<string, (payload: unknown) => void>>(() => ({}))
const connections = vi.hoisted(
  () =>
    [] as Array<{
      on: ReturnType<typeof vi.fn>
      onreconnecting: (cb: () => void) => void
      onreconnected: (cb: () => void) => void
      onclose: (cb: () => void) => void
      start: ReturnType<typeof vi.fn>
      stop: ReturnType<typeof vi.fn>
      state: string
      handlers: Record<string, (payload: unknown) => void>
      triggerReconnecting: () => void
      triggerReconnected: () => void
      triggerClose: () => void
    }>,
)

const USER_A = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const USER_B = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'

let customStartImplementation: ((conn: (typeof connections)[0]) => Promise<void>) | null = null

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    configureLogging: vi.fn().mockReturnThis(),
    build: vi.fn(() => {
      const connHandlers: Record<string, (payload: unknown) => void> = {}
      let reconnectingCb: (() => void) | null = null
      let reconnectedCb: (() => void) | null = null
      let closeCb: (() => void) | null = null

      const conn = {
        on: vi.fn((event: string, cb: (payload: unknown) => void) => {
          connHandlers[event] = cb
          handlers[event] = cb
        }),
        onreconnecting: vi.fn((cb: () => void) => {
          reconnectingCb = cb
        }),
        onreconnected: vi.fn((cb: () => void) => {
          reconnectedCb = cb
        }),
        onclose: vi.fn((cb: () => void) => {
          closeCb = cb
        }),
        start: vi.fn(async () => {
          if (customStartImplementation) {
            await customStartImplementation(conn)
          } else {
            conn.state = 'Connected'
          }
        }),
        stop: vi.fn(async () => {
          conn.state = 'Disconnected'
        }),
        state: 'Disconnected',
        handlers: connHandlers,
        triggerReconnecting: () => {
          conn.state = 'Reconnecting'
          reconnectingCb?.()
        },
        triggerReconnected: () => {
          conn.state = 'Connected'
          reconnectedCb?.()
        },
        triggerClose: () => {
          conn.state = 'Disconnected'
          closeCb?.()
        },
      }
      connections.push(conn)
      return conn
    }),
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
    }, USER_A)

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
    }, USER_A)

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
    }, USER_A)

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

  it('does not toast for AssetProcessingUpdated', () => {
    const unsubscribe = subscribeProcessingHub(() => {}, USER_A)
    handlers.AssetProcessingUpdated?.({
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
      type: 'MALWARE_SCAN',
      status: 'SUCCEEDED',
      stage: 'READY',
      updatedAt: '2026-08-25T12:00:00Z',
    })
    expect(toast.info).not.toHaveBeenCalled()
    unsubscribe()
  })
})

describe('notification-hub durable processing notifications', () => {
  beforeEach(() => {
    vi.mocked(toast.info).mockClear()
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

  function terminalPayload(notificationId: string) {
    return {
      notificationId,
      assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      assetVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      processingStatus: 'READY' as const,
      assetTitle: 'Forest Pack',
    }
  }

  it('shows one toast for a durable processing notification', () => {
    const unsubscribe = subscribeNotificationHub(() => {}, USER_A)
    handlers.AssetProcessingReady?.(terminalPayload('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'))
    expect(toast.info).toHaveBeenCalledTimes(1)
    expect(toast.info).toHaveBeenCalledWith(expect.stringMatching(/listing ready/i))
    unsubscribe()
  })

  it('dedupes toast by notification id', () => {
    const payload = terminalPayload('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')
    const unsubscribe = subscribeNotificationHub(() => {}, USER_A)
    handlers.AssetProcessingReady?.(payload)
    handlers.AssetProcessingReady?.(payload)
    expect(toast.info).toHaveBeenCalledTimes(1)
    unsubscribe()
  })

  it('ignores malformed processing notification payloads', () => {
    const unsubscribe = subscribeNotificationHub(() => {}, USER_A)
    expect(() => handlers.AssetProcessingRejected?.({ jobId: 'bad' })).not.toThrow()
    expect(toast.info).not.toHaveBeenCalled()
    unsubscribe()
  })

  it('does not toast twice when a terminal processing job update arrives with a durable notification', () => {
    const payload = terminalPayload('cccccccc-cccc-4ccc-8ccc-cccccccccccc')
    const unsubProcessing = subscribeProcessingHub(() => {}, USER_A)
    const unsubNotify = subscribeNotificationHub(() => {}, USER_A)
    handlers.AssetProcessingUpdated?.({
      jobId: '11111111-1111-4111-8111-111111111111',
      assetId: payload.assetId,
      assetVersionId: payload.assetVersionId,
      type: 'MALWARE_SCAN',
      status: 'SUCCEEDED',
      stage: 'READY',
      updatedAt: '2026-08-25T12:00:00Z',
    })
    handlers.AssetProcessingReady?.(payload)
    expect(toast.info).toHaveBeenCalledTimes(1)
    unsubProcessing()
    unsubNotify()
  })
})

describe('notification-hub identity lifecycle', () => {
  beforeEach(() => {
    vi.mocked(toast.info).mockClear()
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

  it('reuses the connection when the same user remounts within the stop debounce', () => {
    const unsubFirst = subscribeNotificationHub(() => {}, USER_A)
    const conn = connections.at(-1)
    expect(conn).toBeDefined()
    unsubFirst()

    const unsubSecond = subscribeNotificationHub(() => {}, USER_A)
    expect(connections.at(-1)).toBe(conn)
    expect(conn?.stop).not.toHaveBeenCalled()
    unsubSecond()
  })

  it('stops the previous connection immediately when the user identity changes', () => {
    const receivedA: unknown[] = []
    const receivedB: unknown[] = []

    const unsubA = subscribeNotificationHub(() => receivedA.push('invalidate'), USER_A)
    const connA = connections.at(-1)
    expect(connA).toBeDefined()

    unsubA()
    const unsubB = subscribeNotificationHub(() => receivedB.push('invalidate'), USER_B)
    const connB = connections.at(-1)

    expect(connB).toBeDefined()
    expect(connB).not.toBe(connA)
    expect(connA?.stop).toHaveBeenCalled()
    expect(connB?.start).toHaveBeenCalled()

    vi.mocked(toast.info).mockClear()
    connA?.handlers.AssetProcessingReady?.({
      notificationId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      assetVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      processingStatus: 'READY',
      assetTitle: 'From A',
    })
    expect(toast.info).not.toHaveBeenCalled()
    expect(receivedA).toHaveLength(0)
    expect(receivedB).toHaveLength(0)

    connB?.handlers.AssetProcessingReady?.({
      notificationId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
      assetId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      assetVersionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      processingStatus: 'READY',
      assetTitle: 'From B',
    })
    expect(toast.info).toHaveBeenCalledTimes(1)
    expect(receivedB).toEqual(['invalidate'])
    unsubB()
  })
})

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('notification-hub connection state synchronization', () => {
  beforeEach(() => {
    customStartImplementation = null
    _resetNotificationHubForTest()
    _resetHubConnectionStateForTest()
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ hubToken: 'test-token' }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )
  })

  afterEach(() => {
    customStartImplementation = null
    vi.unstubAllGlobals()
  })

  it('transitions disconnected -> connecting -> connected with deferred start resolution', async () => {
    let resolveStart!: () => void
    let connInstance: (typeof connections)[0] | undefined
    const startDeferred = new Promise<void>((resolve) => {
      resolveStart = () => {
        if (connInstance) connInstance.state = 'Connected'
        resolve()
      }
    })
    customStartImplementation = (c) => {
      connInstance = c
      return startDeferred
    }

    expect(getHubConnectionState()).toBe('disconnected')

    const unsub = subscribeNotificationHub(() => {}, USER_A)
    const conn = connections.at(-1)
    expect(conn).toBeDefined()
    expect(conn?.start).toHaveBeenCalled()

    // While start is pending, state is connecting
    expect(getHubConnectionState()).toBe('connecting')

    // Resolving start transitions to connected
    resolveStart()
    await flushMicrotasks()
    expect(getHubConnectionState()).toBe('connected')

    unsub()
  })

  it('transitions disconnected -> connecting -> disconnected when start fails/rejects', async () => {
    let rejectStart!: (err: Error) => void
    const startDeferred = new Promise<void>((_, reject) => {
      rejectStart = reject
    })
    customStartImplementation = () => startDeferred

    expect(getHubConnectionState()).toBe('disconnected')

    const unsub = subscribeNotificationHub(() => {}, USER_A)
    const conn = connections.at(-1)
    expect(conn).toBeDefined()
    expect(conn?.start).toHaveBeenCalled()

    // While start is pending, state is connecting
    expect(getHubConnectionState()).toBe('connecting')

    // Rejecting start transitions back to disconnected
    rejectStart(new Error('Connection failure'))
    await flushMicrotasks()
    expect(getHubConnectionState()).toBe('disconnected')

    unsub()
  })

  it('handles reconnecting, reconnected, and close transitions with exact state assertions', async () => {
    const unsub = subscribeNotificationHub(() => {}, USER_A)
    const conn = connections.at(-1)
    expect(conn).toBeDefined()
    await flushMicrotasks()
    expect(getHubConnectionState()).toBe('connected')

    conn?.triggerReconnecting()
    expect(getHubConnectionState()).toBe('reconnecting')

    conn?.triggerReconnected()
    expect(getHubConnectionState()).toBe('connected')

    conn?.triggerClose()
    expect(getHubConnectionState()).toBe('disconnected')

    unsub()
  })

  it('ignores stale connection callbacks after identity change creates a new connection', async () => {
    const unsubA = subscribeNotificationHub(() => {}, USER_A)
    const connA = connections.at(-1)
    await flushMicrotasks()
    expect(getHubConnectionState()).toBe('connected')

    // Switch user identity to USER_B -> creates connB and tears down connA
    const unsubB = subscribeNotificationHub(() => {}, USER_B)
    const connB = connections.at(-1)
    expect(connB).not.toBe(connA)
    await flushMicrotasks()
    expect(getHubConnectionState()).toBe('connected')

    // Stale callbacks on old connA must be ignored by the identity guard
    connA?.triggerReconnecting()
    expect(getHubConnectionState()).toBe('connected') // NOT reconnecting, because connA is stale

    connA?.triggerClose()
    expect(getHubConnectionState()).toBe('connected') // NOT disconnected

    // Active connB lifecycle transitions work normally
    connB?.triggerReconnecting()
    expect(getHubConnectionState()).toBe('reconnecting')

    connB?.triggerReconnected()
    expect(getHubConnectionState()).toBe('connected')

    unsubA()
    unsubB()
  })
})
