import { describe, expect, it } from 'vitest'
import { createOperationTracker } from '@/lib/seller/draft-operations'

describe('createOperationTracker', () => {
  it('reuses the same operation ID for retries of the same logical payload', () => {
    const tracker = createOperationTracker()
    const first = tracker.idFor('payload-a')
    expect(tracker.idFor('payload-a')).toBe(first)
  })

  it('mints a new operation ID when the payload changes', () => {
    const tracker = createOperationTracker()
    const first = tracker.idFor('payload-a')
    const second = tracker.idFor('payload-b')
    expect(second).not.toBe(first)
    expect(tracker.idFor('payload-b')).toBe(second)
  })

  it('starts a new operation after an explicit reset', () => {
    const tracker = createOperationTracker()
    const first = tracker.idFor('payload-a')
    tracker.reset()
    expect(tracker.idFor('payload-a')).not.toBe(first)
  })

  it('produces UUID-shaped identifiers', () => {
    const tracker = createOperationTracker()
    expect(tracker.idFor('payload')).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    )
  })
})
