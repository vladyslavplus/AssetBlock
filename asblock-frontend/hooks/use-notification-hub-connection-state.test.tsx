import { describe, expect, it, beforeEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useNotificationHubConnectionState } from '@/hooks/use-notification-hub-connection-state'
import {
  setHubConnectionState,
  _resetHubConnectionStateForTest,
} from '@/lib/notifications/hub-connection-state'

describe('useNotificationHubConnectionState', () => {
  beforeEach(() => {
    _resetHubConnectionStateForTest()
  })

  it('reflects initial state and reacts to updates', () => {
    const { result } = renderHook(() => useNotificationHubConnectionState())

    expect(result.current).toBe('disconnected')

    act(() => {
      setHubConnectionState('connecting')
    })
    expect(result.current).toBe('connecting')

    act(() => {
      setHubConnectionState('connected')
    })
    expect(result.current).toBe('connected')

    act(() => {
      setHubConnectionState('reconnecting')
    })
    expect(result.current).toBe('reconnecting')

    act(() => {
      setHubConnectionState('disconnected')
    })
    expect(result.current).toBe('disconnected')
  })
})
