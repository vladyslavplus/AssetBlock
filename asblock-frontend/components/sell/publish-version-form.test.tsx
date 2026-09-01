import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { PublishVersionForm } from '@/components/sell/publish-version-form'
import type * as sellerApi from '@/lib/seller/seller-api'
import { createTestQueryClient } from '@/test/query-client'

const publishSellerAssetVersion = vi.hoisted(() => vi.fn())
const toastError = vi.hoisted(() => vi.fn())
const toastSuccess = vi.hoisted(() => vi.fn())

vi.mock('@/lib/seller/seller-api', async () => {
  const actual = await vi.importActual<typeof sellerApi>('@/lib/seller/seller-api')
  return { ...actual, publishSellerAssetVersion }
})

vi.mock('sonner', () => ({
  toast: { error: toastError, success: toastSuccess },
}))

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

function renderPublish() {
  const queryClient = createTestQueryClient()
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  return {
    queryClient,
    invalidate,
    ...render(
      <QueryClientProvider client={queryClient}>
        <PublishVersionForm assetId={assetId} />
      </QueryClientProvider>,
    ),
  }
}

function setPackageFile(file: File) {
  const input = document.getElementById('publish-version-file')
  if (!(input instanceof HTMLInputElement)) {
    throw new Error('Package file input was not rendered.')
  }
  fireEvent.change(input, { target: { files: [file] } })
}

describe('PublishVersionForm', () => {
  beforeEach(() => {
    publishSellerAssetVersion.mockReset()
    toastError.mockReset()
    toastSuccess.mockReset()
  })

  it('shows processing toast after a successful upload and does not invalidate library queries', async () => {
    const user = userEvent.setup()
    publishSellerAssetVersion.mockResolvedValueOnce({ ok: true, versionId: 'version-1' })
    const { invalidate } = renderPublish()

    await user.type(screen.getByLabelText(/release notes/i), 'Security scan pending')
    setPackageFile(new File(['zip'], 'pack.zip', { type: 'application/zip' }))
    await user.click(screen.getByRole('button', { name: /upload version/i }))

    await waitFor(() => {
      expect(publishSellerAssetVersion).toHaveBeenCalledTimes(1)
    })
    expect(toastSuccess).toHaveBeenCalledWith('New version uploaded. Security processing started.')

    const calls = invalidate.mock.calls.map((c) => JSON.stringify(c[0]))
    expect(calls.some((k) => k.includes('library'))).toBe(false)
    expect(calls.some((k) => k.includes('seller'))).toBe(true)
  })
})
