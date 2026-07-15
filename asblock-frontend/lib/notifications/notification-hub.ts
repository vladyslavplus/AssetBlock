import * as signalR from '@microsoft/signalr'

import { formatHubToastMessage } from '@/lib/notifications/notification-ui'
import { getNotificationsHubUrl } from '@/lib/notifications/notifications-hub-url'
import { toast } from 'sonner'

const HUB_METHODS = ['PurchaseCompleted', 'DownloadReady', 'AssetSold', 'ReviewReceived'] as const

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

const invalidateHandlers = new Set<InvalidateFn>()

let hubConnection: signalR.HubConnection | null = null
let startPromise: Promise<void> | null = null
let disconnectRequested = false
let pendingStopTimer: ReturnType<typeof setTimeout> | null = null

function cancelPendingStop(): void {
  if (pendingStopTimer != null) {
    clearTimeout(pendingStopTimer)
    pendingStopTimer = null
  }
}

function dispatchHubEvent(method: string, payload: unknown): void {
  toast.info(formatHubToastMessage(method, payload))
  for (const fn of invalidateHandlers) {
    try {
      fn()
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
        const data = (await res.json()) as { accessToken?: string }
        if (!data.accessToken) {
          throw new Error('SignalR token missing')
        }
        return data.accessToken
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
    conn.on(m, (payload: unknown) => dispatchHubEvent(m, payload))
  }

  return conn
}

function ensureConnection(): void {
  cancelPendingStop()
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
  if (invalidateHandlers.size > 0) {
    return
  }
  pendingStopTimer = setTimeout(() => {
    pendingStopTimer = null
    if (invalidateHandlers.size === 0) {
      void tearDownConnection()
    }
  }, STOP_DEBOUNCE_MS)
}

/**
 * Subscribe to the shared notifications hub. Multiple callers share one connection; unsubscribing uses a debounced
 * disconnect so dev remounts do not spam negotiate errors.
 */
export function subscribeNotificationHub(onInvalidate: InvalidateFn): () => void {
  invalidateHandlers.add(onInvalidate)
  ensureConnection()

  return () => {
    invalidateHandlers.delete(onInvalidate)
    if (invalidateHandlers.size === 0) {
      scheduleStopIfIdle()
    }
  }
}
