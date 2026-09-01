import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { render, type RenderOptions, type RenderResult } from '@testing-library/react'
import type { ReactElement, ReactNode } from 'react'

import { AuthProvider } from '@/components/auth/auth-context'
import type { SessionUser } from '@/lib/auth/auth-types'
import { authKeys } from '@/lib/auth/auth-query'
import { createTestQueryClient } from '@/test/query-client'

export interface RenderWithQueryClientOptions extends Omit<RenderOptions, 'wrapper'> {
  queryClient?: QueryClient
}

export interface RenderWithQueryClientResult extends RenderResult {
  queryClient: QueryClient
}

export function renderWithQueryClient(
  ui: ReactElement,
  options?: RenderWithQueryClientOptions,
): RenderWithQueryClientResult {
  const queryClient = options?.queryClient ?? createTestQueryClient()

  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  }

  const { queryClient: _qc, ...restOptions } = options ?? {}

  const result = render(ui, { wrapper: Wrapper, ...restOptions })
  return { queryClient, ...result }
}

export interface RenderWithProvidersOptions extends RenderWithQueryClientOptions {
  authUser?: SessionUser | null
  /** When true, allows AuthProvider to perform a real background fetch instead of preloading session cache. */
  loadSession?: boolean
}

export type RenderWithProvidersResult = RenderWithQueryClientResult

export function renderWithProviders(
  ui: ReactElement,
  options?: RenderWithProvidersOptions,
): RenderWithProvidersResult {
  const queryClient = options?.queryClient ?? createTestQueryClient()

  if (!options?.loadSession) {
    const authUser = options && 'authUser' in options ? options.authUser : null
    queryClient.setQueryData(authKeys.session(), authUser ?? null)
  }

  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <AuthProvider>{children}</AuthProvider>
      </QueryClientProvider>
    )
  }

  const { queryClient: _qc, authUser: _au, loadSession: _ls, ...restOptions } = options ?? {}

  const result = render(ui, { wrapper: Wrapper, ...restOptions })
  return { queryClient, ...result }
}
