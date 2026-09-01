import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  getHubConnectionState,
  getServerHubConnectionState,
  setHubConnectionState,
  subscribeHubConnectionState,
  _resetHubConnectionStateForTest,
} from '@/lib/notifications/hub-connection-state'

describe('hub connection state store', () => {
  beforeEach(() => {
    _resetHubConnectionStateForTest()
  })

  it('defaults to disconnected', () => {
    expect(getHubConnectionState()).toBe('disconnected')
    expect(getServerHubConnectionState()).toBe('disconnected')
  })

  it('notifies subscribers on state transitions', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeHubConnectionState(listener)

    setHubConnectionState('connecting')
    expect(getHubConnectionState()).toBe('connecting')
    expect(listener).toHaveBeenCalledTimes(1)

    setHubConnectionState('connected')
    expect(getHubConnectionState()).toBe('connected')
    expect(listener).toHaveBeenCalledTimes(2)

    setHubConnectionState('reconnecting')
    expect(getHubConnectionState()).toBe('reconnecting')
    expect(listener).toHaveBeenCalledTimes(3)

    setHubConnectionState('disconnected')
    expect(getHubConnectionState()).toBe('disconnected')
    expect(listener).toHaveBeenCalledTimes(4)

    unsubscribe()
    setHubConnectionState('connecting')
    expect(listener).toHaveBeenCalledTimes(4)
  })

  it('does not notify when setting the same state', () => {
    const listener = vi.fn()
    subscribeHubConnectionState(listener)

    setHubConnectionState('disconnected')
    expect(listener).not.toHaveBeenCalled()

    setHubConnectionState('connected')
    expect(listener).toHaveBeenCalledTimes(1)

    setHubConnectionState('connected')
    expect(listener).toHaveBeenCalledTimes(1)
  })
})
