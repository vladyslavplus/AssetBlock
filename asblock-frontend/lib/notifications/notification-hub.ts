import * as signalR from '@microsoft/signalr'

import { formatHubToastMessage } from '@/lib/notifications/notification-ui'
import { getNotificationsHubUrl } from '@/lib/notifications/notifications-hub-url'
import {
  assetProcessingUpdateMessageSchema,
  type AssetProcessingUpdateMessage,
} from '@/lib/seller/seller-processing-schemas'
import { z } from 'zod'
import { toast } from 'sonner'

const HUB_METHODS = [
  'OrderReady',
  'DownloadReady',
  'AssetSold',
  'ReviewReceived',
  'AssetProcessingReady',
  'AssetProcessingRejected',
  'AssetProcessingFailed',
] as const
const ASSET_PROCESSING_UPDATED_METHOD = 'AssetProcessingUpdated'
const PROCESSING_NOTIFICATION_METHODS = new Set<string>([
  'AssetProcessingReady',
  'AssetProcessingRejected',
  'AssetProcessingFailed',
])

const seenNotificationIds = new Set<string>()
const MAX_SEEN_NOTIFICATION_IDS = 200

/** When the last subscriber leaves, delay stop so React Strict Mode / HMR remounts do not abort negotiate. */
const STOP_DEBOUNCE_MS = 500

/** Only dev lifecycle / intentional stop — do not hide other "failed to start" errors. */
const NEGOTIATION_ABORT_MESSAGE =
  /stopped during negotiation|connection was stopped during negotiation/i

function createHubLogger(): signalR.ILogger {
  return {
    log(logLevel, message) {
      if (NEGOTIATION_ABORT_MESSAGE.test(message)) {
        return
      }
      if (logLevel <= signalR.LogLevel.Debug) {
        return
      }
      if (logLevel === signalR.LogLevel.Information && process.env.NODE_ENV === 'development') {
        return
      }
      const prefix = '[SignalR]'
      if (logLevel === signalR.LogLevel.Warning) {
        console.warn(prefix, message)
        return
      }
      if (logLevel >= signalR.LogLevel.Error) {
        console.error(prefix, message)
      }
    },
  }
}

type InvalidateFn = () => void
export type ProcessingUpdateFn = (message: AssetProcessingUpdateMessage) => void

const invalidateHandlers = new Set<InvalidateFn>()
const processingUpdateHandlers = new Set<ProcessingUpdateFn>()

let hubConnection: signalR.HubConnection | null = null
let startPromise: Promise<void> | null = null
let disconnectRequested = false
let pendingStopTimer: ReturnType<typeof setTimeout> | null = null
let boundUserId: string | null = null

function cancelPendingStop(): void {
  if (pendingStopTimer != null) {
    clearTimeout(pendingStopTimer)
    pendingStopTimer = null
  }
}

const processingNotificationPayloadSchema = z
  .object({
    notificationId: z.string().uuid(),
    assetId: z.string().uuid(),
    assetVersionId: z.string().uuid(),
    processingStatus: z.enum(['READY', 'REJECTED', 'PROCESSING_FAILED']),
    assetTitle: z.string().min(1).max(500),
  })
  .strict()

function notificationIdFromPayload(payload: unknown): string | undefined {
  if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) {
    return undefined
  }
  const value = (payload as Record<string, unknown>).notificationId
  return typeof value === 'string' && value.length > 0 ? value : undefined
}

function rememberNotificationId(id: string): boolean {
  if (seenNotificationIds.has(id)) {
    return false
  }
  if (seenNotificationIds.size >= MAX_SEEN_NOTIFICATION_IDS) {
    const first = seenNotificationIds.values().next().value
    if (typeof first === 'string') {
      seenNotificationIds.delete(first)
    }
  }
  seenNotificationIds.add(id)
  return true
}

function dispatchHubEvent(method: string, payload: unknown): void {
  if (PROCESSING_NOTIFICATION_METHODS.has(method)) {
    const parsed = processingNotificationPayloadSchema.safeParse(payload)
    if (!parsed.success) {
      return
    }
    payload = parsed.data
  }

  const notificationId = notificationIdFromPayload(payload)
  const isNew = notificationId ? rememberNotificationId(notificationId) : true
  if (isNew) {
    toast.info(formatHubToastMessage(method, payload))
  }
  for (const fn of invalidateHandlers) {
    try {
      fn()
    } catch {
      /* subscriber must not break hub */
    }
  }
}

function dispatchProcessingEvent(rawPayload: unknown): void {
  const result = assetProcessingUpdateMessageSchema.safeParse(rawPayload)
  if (!result.success) {
    // Malformed event ignored safely without throwing
    return
  }
  for (const fn of processingUpdateHandlers) {
    try {
      fn(result.data)
    } catch {
      /* subscriber must not break hub */
    }
  }
}

function buildConnection(): signalR.HubConnection {
  const hubUrl = getNotificationsHubUrl()
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, {
      accessTokenFactory: async () => {
        const res = await fetch('/api/auth/signalr-access', {
          credentials: 'include',
          cache: 'no-store',
        })
        if (!res.ok) {
          throw new Error('SignalR token unavailable')
        }
        const data = (await res.json()) as { hubToken?: string }
        if (!data.hubToken) {
          throw new Error('SignalR hub token missing')
        }
        return data.hubToken
      },
      transport:
        signalR.HttpTransportType.WebSockets |
        signalR.HttpTransportType.ServerSentEvents |
        signalR.HttpTransportType.LongPolling,
    })
    .withAutomaticReconnect()
    .configureLogging(createHubLogger())
    .build()

  for (const m of HUB_METHODS) {
    conn.on(m, (payload: unknown) => {
      if (hubConnection !== conn) {
        return
      }
      dispatchHubEvent(m, payload)
    })
  }

  conn.on(ASSET_PROCESSING_UPDATED_METHOD, (payload: unknown) => {
    if (hubConnection !== conn) {
      return
    }
    dispatchProcessingEvent(payload)
  })

  return conn
}

function bindHubUser(userId: string): void {
  cancelPendingStop()
  if (boundUserId === userId) {
    return
  }
  seenNotificationIds.clear()
  boundUserId = userId
  void tearDownConnection()
}

function ensureConnection(): void {
  cancelPendingStop()
  if (boundUserId == null) {
    return
  }
  if (hubConnection != null) {
    const state = hubConnection.state
    if (
      state === signalR.HubConnectionState.Connected ||
      state === signalR.HubConnectionState.Connecting
    ) {
      return
    }
    if (
      state === signalR.HubConnectionState.Reconnecting ||
      state === signalR.HubConnectionState.Disconnecting
    ) {
      return
    }
  }

  if (hubConnection == null) {
    hubConnection = buildConnection()
  }

  disconnectRequested = false
  if (startPromise != null) {
    return
  }

  const conn = hubConnection
  startPromise = (async () => {
    try {
      await conn.start()
    } catch (err) {
      if (disconnectRequested || hubConnection !== conn) {
        return
      }
      if (err instanceof Error && NEGOTIATION_ABORT_MESSAGE.test(err.message)) {
        return
      }
    } finally {
      startPromise = null
    }
  })()
}

async function tearDownConnection(): Promise<void> {
  cancelPendingStop()
  const conn = hubConnection
  hubConnection = null
  startPromise = null
  if (conn == null) {
    return
  }
  disconnectRequested = true
  try {
    await conn.stop()
  } catch {
    /* stop during negotiate etc. */
  }
}

function scheduleStopIfIdle(): void {
  cancelPendingStop()
  if (invalidateHandlers.size > 0 || processingUpdateHandlers.size > 0) {
    return
  }
  pendingStopTimer = setTimeout(() => {
    pendingStopTimer = null
    if (invalidateHandlers.size === 0 && processingUpdateHandlers.size === 0) {
      void tearDownConnection()
    }
  }, STOP_DEBOUNCE_MS)
}

/**
 * Subscribe to the shared notifications hub. Multiple callers share one connection; unsubscribing uses a debounced
 * disconnect so dev remounts do not spam negotiate errors.
 */
export function subscribeNotificationHub(onInvalidate: InvalidateFn, userId: string): () => void {
  if (!userId) {
    return () => {}
  }
  bindHubUser(userId)
  invalidateHandlers.add(onInvalidate)
  ensureConnection()

  return () => {
    invalidateHandlers.delete(onInvalidate)
    if (invalidateHandlers.size === 0 && processingUpdateHandlers.size === 0) {
      scheduleStopIfIdle()
    }
  }
}

/**
 * Subscribe to real-time AssetProcessingUpdated events on the shared hub connection.
 */
export function subscribeProcessingHub(
  onProcessingUpdate: ProcessingUpdateFn,
  userId: string,
): () => void {
  if (!userId) {
    return () => {}
  }
  bindHubUser(userId)
  processingUpdateHandlers.add(onProcessingUpdate)
  ensureConnection()

  return () => {
    processingUpdateHandlers.delete(onProcessingUpdate)
    if (invalidateHandlers.size === 0 && processingUpdateHandlers.size === 0) {
      scheduleStopIfIdle()
    }
  }
}
