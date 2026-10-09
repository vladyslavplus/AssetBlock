/** Tracks a mutation operation ID across retries of the same logical operation. */
export interface OperationTracker {
  /** Returns the operation ID for the given logical payload key. */
  idFor(payloadKey: string): string
  /** Forces the next call to `idFor` to mint a fresh operation ID. */
  reset(): void
}

export function createOperationTracker(): OperationTracker {
  let current: { id: string; key: string } | null = null
  return {
    idFor(payloadKey: string): string {
      if (!current || current.key !== payloadKey) {
        current = { id: crypto.randomUUID(), key: payloadKey }
      }
      return current.id
    },
    reset(): void {
      current = null
    },
  }
}
