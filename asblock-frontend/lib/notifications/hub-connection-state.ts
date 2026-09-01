export type HubConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

type HubStateListener = () => void

let currentHubConnectionState: HubConnectionState = 'disconnected'
const listeners = new Set<HubStateListener>()

export function getHubConnectionState(): HubConnectionState {
  return currentHubConnectionState
}

export function getServerHubConnectionState(): HubConnectionState {
  return 'disconnected'
}

export function setHubConnectionState(nextState: HubConnectionState): void {
  if (currentHubConnectionState === nextState) {
    return
  }
  currentHubConnectionState = nextState
  for (const listener of listeners) {
    try {
      listener()
    } catch {
      /* listener errors must not break state publishing */
    }
  }
}

export function subscribeHubConnectionState(listener: HubStateListener): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** Testing helper to reset store state between isolated unit tests. */
export function _resetHubConnectionStateForTest(): void {
  currentHubConnectionState = 'disconnected'
  listeners.clear()
}
