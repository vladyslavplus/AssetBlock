import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AssetUploadForm } from '@/components/sell/asset-upload-form'
import { renderWithQueryClient } from '@/test/render'
import { verifiedSeller } from '@/test/session-user'
import type * as SellerDraftApiModule from '@/lib/seller/seller-draft-api'
import type * as CatalogQueryModule from '@/lib/catalog/catalog-query'

const createSellerDraft = vi.hoisted(() => vi.fn())
const fetchCatalogFacets = vi.hoisted(() => vi.fn())
const useAuth = vi.hoisted(() => vi.fn())
const routerPush = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: routerPush, replace: vi.fn(), refresh: vi.fn() }),
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('@/lib/seller/seller-draft-api', async () => {
  const actual = await vi.importActual<typeof SellerDraftApiModule>('@/lib/seller/seller-draft-api')
  return { ...actual, createSellerDraft }
})

vi.mock('@/lib/catalog/catalog-query', async () => {
  const actual = await vi.importActual<typeof CatalogQueryModule>('@/lib/catalog/catalog-query')
  return { ...actual, fetchCatalogFacets }
})

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }))

const UUID_CATEGORY = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const UUID_CREATED = '123e4567-e89b-12d3-a456-426614174999'

function renderForm() {
  return renderWithQueryClient(<AssetUploadForm />)
}

async function fillForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/^title$/i), 'Forest Pack')
  await user.type(screen.getByLabelText('Price in USD'), '15')
  await user.selectOptions(screen.getByLabelText(/^category$/i), UUID_CATEGORY)
}

async function submitForm(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /^create draft$/i }))
}

describe('AssetUploadForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      isModerator: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    fetchCatalogFacets.mockResolvedValue({
      categories: [{ id: UUID_CATEGORY, name: '3D' }],
      tags: [],
    })
  })

  it('creates a draft from metadata only, without a file', async () => {
    const user = userEvent.setup()
    createSellerDraft.mockResolvedValue({
      ok: true,
      value: {
        assetId: UUID_CREATED,
        workspaceId: '123e4567-e89b-12d3-a456-426614174001',
        workspaceRevision: 1,
        replayed: false,
      },
    })
    renderForm()

    await fillForm(user)
    await submitForm(user)

    await waitFor(() => {
      expect(createSellerDraft).toHaveBeenCalledTimes(1)
    })
    const body = createSellerDraft.mock.calls[0][0] as Record<string, unknown>
    expect(body.title).toBe('Forest Pack')
    expect(body.categoryId).toBe(UUID_CATEGORY)
    expect(body.operationId).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    )
    expect(routerPush).toHaveBeenCalledWith(`/sell/assets/${UUID_CREATED}/edit`)
    expect(screen.queryByLabelText(/package file/i)).not.toBeInTheDocument()
  })

  it('reuses the operation ID when retrying the same logical creation', async () => {
    const user = userEvent.setup()
    createSellerDraft.mockRejectedValueOnce(new TypeError('Network failed'))
    createSellerDraft.mockResolvedValueOnce({
      ok: true,
      value: {
        assetId: UUID_CREATED,
        workspaceId: '123e4567-e89b-12d3-a456-426614174001',
        workspaceRevision: 1,
        replayed: false,
      },
    })
    renderForm()

    await fillForm(user)
    await submitForm(user)
    await waitFor(() => expect(createSellerDraft).toHaveBeenCalledTimes(1))
    await submitForm(user)
    await waitFor(() => expect(createSellerDraft).toHaveBeenCalledTimes(2))

    const firstId = (createSellerDraft.mock.calls[0][0] as Record<string, unknown>).operationId
    const retryId = (createSellerDraft.mock.calls[1][0] as Record<string, unknown>).operationId
    expect(retryId).toBe(firstId)
  })

  it('starts a new operation after the payload changes', async () => {
    const user = userEvent.setup()
    createSellerDraft.mockResolvedValue({
      ok: false,
      message: 'Network failed.',
    })
    renderForm()

    await fillForm(user)
    await submitForm(user)
    await waitFor(() => expect(createSellerDraft).toHaveBeenCalledTimes(1))
    await user.type(screen.getByLabelText(/^title$/i), ' v2')
    await submitForm(user)
    await waitFor(() => expect(createSellerDraft).toHaveBeenCalledTimes(2))

    const firstId = (createSellerDraft.mock.calls[0][0] as Record<string, unknown>).operationId
    const changedId = (createSellerDraft.mock.calls[1][0] as Record<string, unknown>).operationId
    expect(changedId).not.toBe(firstId)
  })

  it('shows a sign-in prompt for anonymous users', () => {
    useAuth.mockReturnValue({
      user: null,
      status: 'anonymous',
      isAdmin: false,
      isModerator: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    renderForm()
    expect(screen.getByText(/sign in to create a listing/i)).toBeInTheDocument()
  })
})
