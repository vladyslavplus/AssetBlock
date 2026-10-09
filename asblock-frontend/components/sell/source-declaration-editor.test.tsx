import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { toast } from 'sonner'
import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { SourceDeclarationEditor } from '@/components/sell/source-declaration-editor'
import { renderWithQueryClient } from '@/test/render'
import type * as SellerDraftApiModule from '@/lib/seller/seller-draft-api'
import { sellerKeys } from '@/lib/seller/seller-query'

const saveSellerDeclaration = vi.hoisted(() => vi.fn())

vi.mock('@/lib/seller/seller-draft-api', async () => {
  const actual = await vi.importActual<typeof SellerDraftApiModule>('@/lib/seller/seller-draft-api')
  return { ...actual, saveSellerDeclaration }
})

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }))

const UUID_ASSET = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const UUID_WORKSPACE = '123e4567-e89b-12d3-a456-426614174001'
const UUID_VERSION = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'

const emptyDeclaration = {
  ownContributionSummary: '',
  ownChanges: null,
  earlierWork: null,
  redistributionAcknowledged: false,
  disclosurePolicyVersion: 'disclosure-v1',
  components: [],
}

const serverState = {
  revision: 1,
  declaration: null as Record<string, unknown> | null,
  complete: false,
  failGet: false,
}

function declarationResponse() {
  return {
    assetId: UUID_ASSET,
    assetVersionId: null,
    workspaceId: UUID_WORKSPACE,
    workspaceRevision: serverState.revision,
    headRevision: serverState.revision - 1,
    declaration: serverState.declaration,
    declarationComplete: serverState.complete,
  }
}

function stubDeclarationFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/declaration')) {
        if (serverState.failGet) {
          return new Response(JSON.stringify({ title: 'Backend unavailable' }), {
            status: 503,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        return new Response(JSON.stringify(declarationResponse()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('{}', { status: 200 })
    }),
  )
}

function commitDeclaration(body: Record<string, unknown>) {
  serverState.declaration = body.declaration as Record<string, unknown>
  serverState.revision += 1
  serverState.complete = true
  return {
    ok: true as const,
    value: {
      workspaceId: UUID_WORKSPACE,
      workspaceRevision: serverState.revision,
      headRevision: serverState.revision - 1,
      replayed: false,
    },
  }
}

function renderEditor(versionId: string | null = null) {
  return renderWithQueryClient(
    <SourceDeclarationEditor assetId={UUID_ASSET} versionId={versionId} />,
  )
}

describe('SourceDeclarationEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    serverState.revision = 1
    serverState.declaration = null
    serverState.complete = false
    serverState.failGet = false
    stubDeclarationFetch()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('saves an incomplete declaration (empty summary, no components) without blocking', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    renderEditor()

    await waitFor(() => {
      expect(screen.getByText(/incomplete — saving is allowed/i)).toBeInTheDocument()
    })
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => {
      expect(saveSellerDeclaration).toHaveBeenCalledTimes(1)
    })
    const call = saveSellerDeclaration.mock.calls[0]
    expect(call[0]).toBe(UUID_ASSET)
    expect(call[1]).toBeNull()
    const body = call[2] as Record<string, unknown>
    expect(body.operationId).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    )
    expect(body.expectedWorkspaceRevision).toBe(1)
    const declaration = body.declaration as Record<string, unknown>
    expect(declaration.ownContributionSummary).toBe('')
    expect((declaration.components as unknown[]).length).toBe(0)
  })

  it('keeps UNKNOWN license and third-party origin as stored data', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.click(screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^component name$/i), 'left-pad')
    await user.type(screen.getByLabelText(/^source url/i), 'https://example.com/left-pad')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => {
      expect(saveSellerDeclaration).toHaveBeenCalledTimes(1)
    })
    const declaration = (saveSellerDeclaration.mock.calls[0][2] as Record<string, unknown>)
      .declaration as Record<string, unknown>
    const components = declaration.components as Array<Record<string, unknown>>
    expect(components).toHaveLength(1)
    expect(components[0].license).toBe('UNKNOWN')
    expect(components[0].origin).toBe('THIRD_PARTY')
    expect(components[0].componentId).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    )
  })

  it('saves against the revision the edits were based on and shows the stale alert on 409', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockResolvedValue({
      ok: false,
      message: 'The draft workspace changed. Refresh and try again.',
      code: 'ERR_MODERATION_WORKSPACE_STALE',
    })
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Original wording')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => {
      expect(screen.getByText(/declaration changed on the server/i)).toBeInTheDocument()
    })
    expect(screen.getByLabelText(/^own contribution summary$/i)).toHaveValue('Original wording')
    expect(saveSellerDeclaration).toHaveBeenCalledTimes(1)
    expect(
      (saveSellerDeclaration.mock.calls[0][2] as Record<string, unknown>).expectedWorkspaceRevision,
    ).toBe(1)
  })

  it('retries the same envelope (operationId and revision) after a failed save', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockResolvedValue({
      ok: false,
      message: 'Request failed (502).',
    })
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Retry wording')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(1))
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(2))
    const first = saveSellerDeclaration.mock.calls[0][2] as Record<string, unknown>
    const second = saveSellerDeclaration.mock.calls[1][2] as Record<string, unknown>
    expect(first.operationId).toBe(second.operationId)
    expect(first.expectedWorkspaceRevision).toBe(second.expectedWorkspaceRevision)
    expect(first.expectedWorkspaceRevision).toBe(1)
    expect(second.declaration).toEqual(first.declaration)
  })

  it('keeps entered values when the server snapshot advances while dirty, then reconciles on demand', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'My local edits')

    queryClient.setQueryData(sellerKeys.declaration(UUID_ASSET, null), {
      ...declarationResponse(),
      workspaceRevision: 3,
      declaration: { ...emptyDeclaration, ownContributionSummary: 'Other tab edit' },
    })
    serverState.revision = 3
    serverState.declaration = { ...emptyDeclaration, ownContributionSummary: 'Other tab edit' }

    await waitFor(() => {
      expect(screen.getByText(/declaration changed on the server/i)).toBeInTheDocument()
    })
    expect(screen.getByLabelText(/^own contribution summary$/i)).toHaveValue('My local edits')

    await user.click(screen.getByRole('button', { name: /load latest/i }))
    await waitFor(() => {
      expect(screen.getByLabelText(/^own contribution summary$/i)).toHaveValue('Other tab edit')
    })
    expect(screen.queryByText(/declaration changed on the server/i)).not.toBeInTheDocument()
  })

  it('keeps entered values when a background refetch advances the server while dirty', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Still editing')

    serverState.revision = 5
    serverState.declaration = { ...emptyDeclaration, ownContributionSummary: 'Concurrent edit' }
    queryClient.invalidateQueries({ queryKey: sellerKeys.declaration(UUID_ASSET, null) })

    await waitFor(() => {
      expect(screen.getByText(/declaration changed on the server/i)).toBeInTheDocument()
    })
    expect(screen.getByLabelText(/^own contribution summary$/i)).toHaveValue('Still editing')
  })

  it('binds the redistribution checkbox to the form value in both directions', async () => {
    const user = userEvent.setup()
    serverState.declaration = { ...emptyDeclaration, redistributionAcknowledged: true }
    serverState.complete = true
    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    renderEditor()

    const checkbox = await screen.findByRole('checkbox')
    expect(checkbox).toBeChecked()

    await user.click(checkbox)
    expect(checkbox).not.toBeChecked()

    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(1))
    const declaration = (saveSellerDeclaration.mock.calls[0][2] as Record<string, unknown>)
      .declaration as Record<string, unknown>
    expect(declaration.redistributionAcknowledged).toBe(false)

    await user.click(checkbox)
    expect(checkbox).toBeChecked()
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(2))
    const second = (saveSellerDeclaration.mock.calls[1][2] as Record<string, unknown>)
      .declaration as Record<string, unknown>
    expect(second.redistributionAcknowledged).toBe(true)
  })

  it('rejects more than 20 evidence references without saving', async () => {
    const user = userEvent.setup()
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.click(screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^component name$/i), 'left-pad')
    await user.type(screen.getByLabelText(/^source url/i), 'https://example.com/left-pad')
    const refs = Array.from({ length: 21 }, (_, i) => `ref-${i + 1}`).join(', ')
    await user.click(screen.getByLabelText(/^permission evidence references/i))
    await user.paste(refs)
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => {
      expect(screen.getByText(/at most 20 evidence references are allowed/i)).toBeInTheDocument()
    })
    expect(saveSellerDeclaration).not.toHaveBeenCalled()
  })

  it('shows completeness from the backend snapshot after a save', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    expect(screen.getByText(/incomplete — saving is allowed/i)).toBeInTheDocument()
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Full declaration')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => {
      expect(screen.getByText(/^complete$/i)).toBeInTheDocument()
    })
  })

  it('targets the version-scoped declaration query and API for a version workspace', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    renderEditor(UUID_VERSION)

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Version sources')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))

    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(1))
    expect(saveSellerDeclaration.mock.calls[0][1]).toBe(UUID_VERSION)
    const fetchMock = vi.mocked(fetch)
    const declarationCalls = fetchMock.mock.calls.filter(([url]) =>
      String(url).includes(`/versions/${UUID_VERSION}/declaration`),
    )
    expect(declarationCalls.length).toBeGreaterThan(0)
  })

  it('Load latest forces a server GET and adopts revision 2 despite a fresh cached revision 1', async () => {
    const user = userEvent.setup()
    const staleQueryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
          gcTime: Infinity,
          staleTime: 60_000,
          refetchOnWindowFocus: false,
        },
        mutations: { retry: false },
      },
    })
    saveSellerDeclaration.mockResolvedValue({
      ok: false,
      message: 'The draft workspace changed.',
      code: 'ERR_MODERATION_WORKSPACE_STALE',
    })
    renderWithQueryClient(<SourceDeclarationEditor assetId={UUID_ASSET} versionId={null} />, {
      queryClient: staleQueryClient,
    })

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Local edits')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(1))
    await waitFor(() => {
      expect(screen.getByText(/declaration changed on the server/i)).toBeInTheDocument()
    })

    // Server advanced to revision 2 while the cache still holds revision 1 (staleTime 60s).
    serverState.revision = 2
    serverState.declaration = { ...emptyDeclaration, ownContributionSummary: 'Server rev 2' }
    const fetchMock = vi.mocked(fetch)
    const countGets = () =>
      fetchMock.mock.calls.filter(([url]) => String(url).includes('/declaration')).length

    const getsBefore = countGets()
    await user.click(screen.getByRole('button', { name: /load latest/i }))
    await waitFor(() => {
      expect(screen.getByLabelText(/^own contribution summary$/i)).toHaveValue('Server rev 2')
    })
    expect(countGets()).toBeGreaterThan(getsBefore)
    expect(screen.queryByText(/declaration changed on the server/i)).not.toBeInTheDocument()

    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    await user.type(screen.getByLabelText(/^own contribution summary$/i), ' more')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(2))
    expect(
      (saveSellerDeclaration.mock.calls[1][2] as Record<string, unknown>).expectedWorkspaceRevision,
    ).toBe(2)
  })

  it('keeps local edits and the conflict state when the Load latest GET fails', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockResolvedValue({
      ok: false,
      message: 'The draft workspace changed.',
      code: 'ERR_MODERATION_WORKSPACE_STALE',
    })
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Local edits')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => {
      expect(screen.getByText(/declaration changed on the server/i)).toBeInTheDocument()
    })

    serverState.failGet = true
    await user.click(screen.getByRole('button', { name: /load latest/i }))
    await waitFor(() => {
      expect(vi.mocked(toast.error)).toHaveBeenCalledWith(
        expect.stringMatching(/could not load the latest declaration/i),
      )
    })
    expect(screen.getByLabelText(/^own contribution summary$/i)).toHaveValue('Local edits')
    expect(screen.getByText(/declaration changed on the server/i)).toBeInTheDocument()
  })

  it('keeps the committed revision when the post-save refresh fails; retry performs GET only', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration.mockImplementation(
      (_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
    )
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Committed wording')

    serverState.failGet = true
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(1))
    await waitFor(() => {
      expect(screen.getByText(/status refresh failed/i)).toBeInTheDocument()
    })
    expect(screen.getByText('Completeness not confirmed')).toBeInTheDocument()
    expect(screen.queryByText('Incomplete — saving is allowed')).not.toBeInTheDocument()
    expect(saveSellerDeclaration).toHaveBeenCalledTimes(1)

    // Recovery refreshes the status without re-mutating.
    serverState.failGet = false
    await user.click(screen.getByRole('button', { name: /retry refresh/i }))
    await waitFor(() => {
      expect(screen.getByText(/^complete$/i)).toBeInTheDocument()
    })
    expect(screen.queryByText(/status refresh failed/i)).not.toBeInTheDocument()
    expect(saveSellerDeclaration).toHaveBeenCalledTimes(1)

    // Further edits build on the committed revision (2), not the pre-save revision.
    await user.type(screen.getByLabelText(/^own contribution summary$/i), ' more')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(2))
    const second = saveSellerDeclaration.mock.calls[1][2] as Record<string, unknown>
    expect(second.expectedWorkspaceRevision).toBe(2)
  })

  it('replays the exact envelope when the mutation throws a network error', async () => {
    const user = userEvent.setup()
    saveSellerDeclaration
      .mockRejectedValueOnce(new TypeError('fetch failed'))
      .mockImplementation((_assetId: unknown, _versionId: unknown, body: Record<string, unknown>) =>
        Promise.resolve(commitDeclaration(body)),
      )
    renderEditor()

    await waitFor(() => screen.getByRole('button', { name: /add component/i }))
    await user.type(screen.getByLabelText(/^own contribution summary$/i), 'Net wording')
    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => {
      expect(vi.mocked(toast.error)).toHaveBeenCalledWith(
        expect.stringMatching(/could not reach the server/i),
      )
    })

    await user.click(screen.getByRole('button', { name: /^save sources$/i }))
    await waitFor(() => expect(saveSellerDeclaration).toHaveBeenCalledTimes(2))
    const first = saveSellerDeclaration.mock.calls[0][2] as Record<string, unknown>
    const second = saveSellerDeclaration.mock.calls[1][2] as Record<string, unknown>
    expect(first.operationId).toBe(second.operationId)
    expect(first.expectedWorkspaceRevision).toBe(second.expectedWorkspaceRevision)
    expect(first.expectedWorkspaceRevision).toBe(1)
    expect(second.declaration).toEqual(first.declaration)
  })
})
