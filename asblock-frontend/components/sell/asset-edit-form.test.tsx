import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { toast } from 'sonner'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AssetEditForm } from '@/components/sell/asset-edit-form'
import type * as SellerDraftApiModule from '@/lib/seller/seller-draft-api'
import type * as SellerApiModule from '@/lib/seller/seller-api'
import type { SellerAssetDetail } from '@/lib/seller/seller-asset-schemas'
import { sellerKeys } from '@/lib/seller/seller-query'
import { renderWithQueryClient } from '@/test/render'

const saveSellerDraft = vi.hoisted(() => vi.fn())
const patchSellerAssetPrice = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/sell/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/edit',
  useSearchParams: () => new URLSearchParams(),
}))

vi.mock('@/lib/seller/seller-draft-api', async () => {
  const actual = await vi.importActual<typeof SellerDraftApiModule>('@/lib/seller/seller-draft-api')
  return { ...actual, saveSellerDraft }
})

vi.mock('@/lib/seller/seller-api', async () => {
  const actual = await vi.importActual<typeof SellerApiModule>('@/lib/seller/seller-api')
  return { ...actual, patchSellerAssetPrice }
})

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }))

vi.mock('@/components/sell/source-declaration-editor', () => ({
  SourceDeclarationEditor: () => null,
}))

vi.mock('@/components/sell/seller-asset-versions-section', () => ({
  SellerAssetVersionsSection: () => null,
}))

const ASSET_ID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const CATEGORY_ID = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'

interface DraftMaterial {
  title: string
  description: string | null
  categoryId: string
  tags: string[]
}

const draftState = {
  revision: 1,
  material: {
    title: 'Old Title',
    description: 'Old description',
    categoryId: CATEGORY_ID,
    tags: ['old-tag'],
  } as DraftMaterial,
  failGet: false,
}

function draftSnapshot() {
  return {
    assetId: ASSET_ID,
    workspaceId: '123e4567-e89b-12d3-a456-426614174001',
    workspaceRevision: draftState.revision,
    caseRevision: 0,
    material: { ...draftState.material, tags: [...draftState.material.tags] },
    declaration: null,
    declarationComplete: false,
    latestVersionId: null,
    latestVersionNumber: null,
  }
}

const asset: SellerAssetDetail = {
  id: ASSET_ID,
  title: 'Old Title',
  description: 'Old description',
  price: 12,
  categoryId: CATEGORY_ID,
  categoryName: 'Category',
  authorId: '11111111-1111-4111-8111-111111111111',
  authorUsername: 'seller',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: null,
  tags: ['old-tag'],
  latestVersionId: null,
  latestVersionNumber: null,
  currentReadyVersionId: null,
  publicVersionId: null,
  latestProcessingStatus: 'READY',
  latestProcessingUpdatedAt: '2026-01-01T00:00:00.000Z',
  latestProcessingErrorCode: null,
  latestProcessingErrorSummary: null,
}

function stubFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/draft')) {
        if (draftState.failGet) {
          return new Response(JSON.stringify({ title: 'Backend unavailable' }), {
            status: 503,
            headers: { 'Content-Type': 'application/json' },
          })
        }
        return new Response(JSON.stringify(draftSnapshot()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (url.includes('/api/categories') || url.includes('/api/tags')) {
        return new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 100 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('{}', { status: 200 })
    }),
  )
}

describe('AssetEditForm material adoption', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    draftState.revision = 1
    draftState.failGet = false
    draftState.material = {
      title: 'Old Title',
      description: 'Old description',
      categoryId: CATEGORY_ID,
      tags: ['old-tag'],
    }
    stubFetch()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('adopts a newer snapshot into the clean form and saves revision-2 metadata after a tags edit', async () => {
    const user = userEvent.setup()
    saveSellerDraft.mockResolvedValue({
      ok: true as const,
      value: {
        workspaceId: '123e4567-e89b-12d3-a456-426614174001',
        workspaceRevision: 3,
        headRevision: 3,
        replayed: false,
      },
    })
    const { queryClient } = renderWithQueryClient(<AssetEditForm asset={asset} />)

    await waitFor(() => {
      expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title')
    })

    queryClient.setQueryData(sellerKeys.draft(ASSET_ID), {
      ...draftSnapshot(),
      workspaceRevision: 2,
      material: {
        title: 'New Title',
        description: 'New description',
        categoryId: CATEGORY_ID,
        tags: ['new-tag'],
      },
    })

    await waitFor(() => {
      expect(screen.getByLabelText(/^title$/i)).toHaveValue('New Title')
    })
    expect(screen.getByLabelText(/^description/i)).toHaveValue('New description')
    expect(screen.queryByText(/draft workspace changed/i)).not.toBeInTheDocument()

    await user.type(screen.getByLabelText(/^tags/i), ', extra-tag')
    await user.click(screen.getByRole('button', { name: /save draft/i }))

    await waitFor(() => expect(saveSellerDraft).toHaveBeenCalledTimes(1))
    const body = saveSellerDraft.mock.calls[0][1] as {
      expectedWorkspaceRevision: number
      material: DraftMaterial
    }
    expect(body.expectedWorkspaceRevision).toBe(2)
    expect(body.material.title).toBe('New Title')
    expect(body.material.description).toBe('New description')
    expect(body.material.tags).toEqual(['new-tag', 'extra-tag'])
    expect(patchSellerAssetPrice).not.toHaveBeenCalled()
  })

  it('does not rebase a dirty material editor and disables saving until reconciled', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderWithQueryClient(<AssetEditForm asset={asset} />)

    await waitFor(() => {
      expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title')
    })
    await user.type(screen.getByLabelText(/^title$/i), 'Local Title')

    queryClient.setQueryData(sellerKeys.draft(ASSET_ID), {
      ...draftSnapshot(),
      workspaceRevision: 2,
      material: { ...draftState.material, title: 'New Title' },
    })

    await waitFor(() => {
      expect(screen.getByText(/draft workspace changed/i)).toBeInTheDocument()
    })
    expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old TitleLocal Title')
    expect(screen.getByRole('button', { name: /save draft/i })).toBeDisabled()
    expect(saveSellerDraft).not.toHaveBeenCalled()
  })

  it('preserves an unsaved price when a material snapshot is adopted', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderWithQueryClient(<AssetEditForm asset={asset} />)

    await waitFor(() => {
      expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title')
    })
    const price = screen.getByRole('spinbutton', { name: 'Price in USD' })
    await user.type(price, '33')

    queryClient.setQueryData(sellerKeys.draft(ASSET_ID), {
      ...draftSnapshot(),
      workspaceRevision: 2,
      material: { ...draftState.material, title: 'New Title' },
    })

    await waitFor(() => {
      expect(screen.getByLabelText(/^title$/i)).toHaveValue('New Title')
    })
    // The typed price (12 → 1233) survives the material snapshot adoption.
    expect(screen.getByRole('spinbutton', { name: 'Price in USD' })).toHaveValue(1233)
  })

  it('keeps edits and the conflict state when the Load latest draft GET fails', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderWithQueryClient(<AssetEditForm asset={asset} />)

    await waitFor(() => {
      expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title')
    })
    await user.type(screen.getByLabelText(/^title$/i), 'Local Title')

    queryClient.setQueryData(sellerKeys.draft(ASSET_ID), {
      ...draftSnapshot(),
      workspaceRevision: 2,
      material: { ...draftState.material, title: 'New Title' },
    })
    await waitFor(() => {
      expect(screen.getByText(/draft workspace changed/i)).toBeInTheDocument()
    })

    draftState.failGet = true
    await user.click(screen.getByRole('button', { name: /load latest/i }))
    await waitFor(() => {
      expect(vi.mocked(toast.error)).toHaveBeenCalledWith(
        expect.stringMatching(/could not load the latest draft/i),
      )
    })
    expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old TitleLocal Title')
    expect(screen.getByText(/draft workspace changed/i)).toBeInTheDocument()
  })

  it('keeps the separate price input when explicitly reconciling material', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderWithQueryClient(<AssetEditForm asset={asset} />)
    await waitFor(() => expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title'))
    await user.type(screen.getByLabelText(/^title$/i), ' local')
    await user.type(screen.getByRole('spinbutton', { name: 'Price in USD' }), '33')
    draftState.revision = 2
    draftState.material.title = 'New Title'
    queryClient.setQueryData(sellerKeys.draft(ASSET_ID), draftSnapshot())
    await waitFor(() => expect(screen.getByText(/draft workspace changed/i)).toBeInTheDocument())
    await user.click(screen.getByRole('button', { name: /load latest/i }))
    await waitFor(() => expect(screen.getByLabelText(/^title$/i)).toHaveValue('New Title'))
    expect(screen.getByRole('spinbutton', { name: 'Price in USD' })).toHaveValue(1233)
  })

  it('keeps the exact material envelope after an unconfirmed network outcome', async () => {
    const user = userEvent.setup()
    saveSellerDraft.mockRejectedValue(new TypeError('Network failed'))
    renderWithQueryClient(<AssetEditForm asset={asset} />)
    await waitFor(() => expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title'))
    await user.type(screen.getByLabelText(/^title$/i), ' local')
    await user.click(screen.getByRole('button', { name: /save draft/i }))
    await waitFor(() =>
      expect(screen.getByText(/could not confirm the draft save/i)).toBeInTheDocument(),
    )
    await user.click(screen.getByRole('button', { name: /save draft/i }))
    await waitFor(() => expect(saveSellerDraft).toHaveBeenCalledTimes(2))
    expect(saveSellerDraft.mock.calls[1][1]).toEqual(saveSellerDraft.mock.calls[0][1])
    expect(screen.getByLabelText(/^title$/i)).toHaveValue('Old Title local')
  })
})
