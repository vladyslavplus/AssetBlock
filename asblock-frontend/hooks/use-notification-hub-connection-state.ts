'use client'

import { useSyncExternalStore } from 'react'
import {
  getHubConnectionState,
  getServerHubConnectionState,
  subscribeHubConnectionState,
  type HubConnectionState,
} from '@/lib/notifications/hub-connection-state'

/**
 * Subscribes to the central notification hub connection state.
 * Server snapshot safely returns 'disconnected'.
 */
export function useNotificationHubConnectionState(): HubConnectionState {
  return useSyncExternalStore(
    subscribeHubConnectionState,
    getHubConnectionState,
    getServerHubConnectionState,
  )
}
