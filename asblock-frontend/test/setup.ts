import '@testing-library/jest-dom/vitest'

import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

vi.mock('next/font/google', () => ({
  Geist: () => ({ variable: '--font-geist' }),
  Geist_Mono: () => ({ variable: '--font-geist-mono' }),
  JetBrains_Mono: () => ({ variable: '--font-jetbrains-mono' }),
  Space_Grotesk: () => ({ variable: '--font-space-grotesk' }),
}))

function setupMatchMedia(): void {
  if (typeof window !== 'undefined') {
    window.matchMedia = function matchMedia(query: string) {
      return {
        matches: false,
        media: query || '',
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      } as unknown as MediaQueryList
    }
  }
}

setupMatchMedia()

class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}

vi.stubGlobal('ResizeObserver', ResizeObserverStub)

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  vi.stubGlobal('ResizeObserver', ResizeObserverStub)
  setupMatchMedia()
})
