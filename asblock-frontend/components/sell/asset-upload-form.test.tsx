import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AssetUploadForm } from '@/components/sell/asset-upload-form'
import type * as sellerApi from '@/lib/seller/seller-api'
import { createTestQueryClient } from '@/test/query-client'
import { verifiedSeller } from '@/test/session-user'

const uploadSellerAsset = vi.hoisted(() => vi.fn())
const useAuth = vi.hoisted(() => vi.fn())
const routerPush = vi.hoisted(() => vi.fn())
const toastError = vi.hoisted(() => vi.fn())
const toastSuccess = vi.hoisted(() => vi.fn())

vi.mock('next/navigation', () => ({
  useRouter: () => ({ push: routerPush, refresh: vi.fn(), replace: vi.fn() }),
  usePathname: () => '/sell',
  useSearchParams: () => new URLSearchParams('tab=upload'),
}))

vi.mock('@/components/auth/auth-context', () => ({
  useAuth: () => useAuth(),
}))

vi.mock('@/lib/seller/seller-api', async () => {
  const actual = await vi.importActual<typeof sellerApi>('@/lib/seller/seller-api')
  return { ...actual, uploadSellerAsset }
})

vi.mock('sonner', () => ({
  toast: { error: toastError, success: toastSuccess },
}))

const categoryId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

function renderUpload() {
  const queryClient = createTestQueryClient()
  return {
    queryClient,
    ...render(
      <QueryClientProvider client={queryClient}>
        <AssetUploadForm />
      </QueryClientProvider>,
    ),
  }
}

function setPackageFile(file: File) {
  const input = document.getElementById('upload-file')
  if (!(input instanceof HTMLInputElement)) {
    throw new Error('Package file input was not rendered.')
  }
  fireEvent.change(input, { target: { files: [file] } })
}

describe('AssetUploadForm', () => {
  beforeEach(() => {
    useAuth.mockReturnValue({
      user: verifiedSeller(),
      status: 'authenticated',
      isAdmin: false,
      refresh: vi.fn(),
      logout: vi.fn(),
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('api/categories')) {
          return new Response(
            JSON.stringify({
              items: [{ id: categoryId, name: 'Scripts', slug: 'scripts', description: null }],
              totalCount: 1,
              page: 1,
              pageSize: 100,
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          )
        }
        if (url.includes('api/tags')) {
          return new Response(
            JSON.stringify({ items: [], totalCount: 0, page: 1, pageSize: 100 }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          )
        }
        return new Response('{}', { status: 200 })
      }),
    )
  })

  it('blocks invalid submit and does not call the API', async () => {
    const user = userEvent.setup()
    renderUpload()
    await user.click(screen.getByRole('button', { name: /publish asset/i }))
    expect(await screen.findByText(/title is required/i)).toBeInTheDocument()
    expect(uploadSellerAsset).not.toHaveBeenCalled()
  })

  it('rejects disallowed file types', async () => {
    renderUpload()
    await screen.findByRole('option', { name: 'Scripts' })
    setPackageFile(new File(['nope'], 'malware.exe', { type: 'application/octet-stream' }))
    expect(await screen.findByText(/choose a \.zip/i)).toBeInTheDocument()
  })

  it('maps backend field errors, prevents double submit, then succeeds', async () => {
    const user = userEvent.setup()
    let resolveUpload: (value: unknown) => void = () => {}
    uploadSellerAsset.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveUpload = resolve
        }),
    )
    renderUpload()
    await screen.findByRole('option', { name: 'Scripts' })
    await user.type(screen.getByLabelText(/^title$/i), 'My pack')
    await user.type(screen.getByRole('spinbutton', { name: 'Price in USD' }), '12')
    await user.selectOptions(screen.getByLabelText('Category'), categoryId)
    setPackageFile(new File(['zip'], 'pack.zip', { type: 'application/zip' }))

    const submit = screen.getByRole('button', { name: /publish asset/i })
    await user.click(submit)
    expect(submit).toBeDisabled()
    await user.click(submit)
    expect(uploadSellerAsset).toHaveBeenCalledTimes(1)
    resolveUpload({
      ok: false,
      message: 'Title is taken.',
      fieldErrors: { title: 'Title is taken.' },
    })
    expect(await screen.findByText('Title is taken.')).toBeInTheDocument()
    expect(toastError).toHaveBeenCalled()
    expect(JSON.stringify(toastError.mock.calls)).not.toMatch(/ZodError/)

    uploadSellerAsset.mockResolvedValueOnce({ ok: true, assetId: 'asset-1' })
    await user.click(screen.getByRole('button', { name: /publish asset/i }))
    await waitFor(() => {
      expect(routerPush).toHaveBeenCalledWith('/assets/asset-1')
    })
    expect(toastSuccess).toHaveBeenCalled()
  })
})
