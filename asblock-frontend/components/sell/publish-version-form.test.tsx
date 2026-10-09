import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { PublishVersionForm } from '@/components/sell/publish-version-form'
import { renderWithQueryClient } from '@/test/render'
import type * as SellerApiModule from '@/lib/seller/seller-api'

const publishSellerAssetVersion = vi.hoisted(() => vi.fn())

vi.mock('@/lib/seller/seller-api', async () => {
  const actual = await vi.importActual<typeof SellerApiModule>('@/lib/seller/seller-api')
  return { ...actual, publishSellerAssetVersion }
})

vi.mock('sonner', () => ({ toast: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }))

const UUID_ASSET = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const UUID_WORKSPACE = '123e4567-e89b-12d3-a456-426614174001'

function stubDraftFetch(workspaceRevision: number) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/draft')) {
        return new Response(
          JSON.stringify({
            assetId: UUID_ASSET,
            workspaceId: UUID_WORKSPACE,
            workspaceRevision,
            caseRevision: 1,
            material: {
              title: 'Draft',
              description: null,
              categoryId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
              tags: [],
            },
            declaration: null,
            declarationComplete: false,
            latestVersionId: null,
            latestVersionNumber: null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      return new Response('{}', { status: 200 })
    }),
  )
}

function createArchiveFile(): File {
  return new File([new Uint8Array([1, 2, 3])], 'package.zip', { type: 'application/zip' })
}

async function fillAndUpload(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/^release notes$/i), 'Initial release')
  const fileInput = document.getElementById('publish-version-file') as HTMLInputElement
  await user.upload(fileInput, createArchiveFile())
  await user.click(screen.getByRole('button', { name: /^upload version$/i }))
}

describe('PublishVersionForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    stubDraftFetch(3)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('labels the action as upload, not publication', () => {
    renderWithQueryClient(<PublishVersionForm assetId={UUID_ASSET} />)
    expect(screen.getByRole('heading', { name: /upload new version/i })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: /publish/i })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^upload version$/i })).toBeInTheDocument()
  })

  it('discloses storage and technical checks without claiming analysis or training', () => {
    renderWithQueryClient(<PublishVersionForm assetId={UUID_ASSET} />)
    const disclosure = screen.getByText(/the package is encrypted and stored/i)
    expect(disclosure).toBeInTheDocument()
    expect(screen.getByText(/not used for model training by default/i)).toBeInTheDocument()
    expect(screen.queryByText(/analysis passed/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/moderator approved/i)).not.toBeInTheDocument()
  })

  it('uploads with the fresh workspace identity and expected revision', async () => {
    const user = userEvent.setup()
    publishSellerAssetVersion.mockResolvedValue({
      ok: true,
      versionId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    })
    renderWithQueryClient(<PublishVersionForm assetId={UUID_ASSET} />)

    await fillAndUpload(user)

    await waitFor(() => {
      expect(publishSellerAssetVersion).toHaveBeenCalledTimes(1)
    })
    const [assetId, formData] = publishSellerAssetVersion.mock.calls[0] as [string, FormData]
    expect(assetId).toBe(UUID_ASSET)
    expect(formData.get('workspaceId')).toBe(UUID_WORKSPACE)
    expect(formData.get('expectedWorkspaceRevision')).toBe('3')
    expect(formData.get('file')).toBeInstanceOf(File)
  })

  it('keeps the chosen file and shows a refresh action on 409 workspace stale', async () => {
    const user = userEvent.setup()
    publishSellerAssetVersion.mockResolvedValue({
      ok: false,
      message: 'The draft workspace changed. Refresh and try again.',
      code: 'ERR_MODERATION_WORKSPACE_STALE',
    })
    renderWithQueryClient(<PublishVersionForm assetId={UUID_ASSET} />)

    await fillAndUpload(user)

    await waitFor(() => {
      expect(screen.getByText(/the draft changed while you were working/i)).toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: /refresh/i })).toBeInTheDocument()
    expect(screen.getByText('package.zip')).toBeInTheDocument()
    expect(publishSellerAssetVersion).toHaveBeenCalledTimes(1)
  })

  it('blocks upload while sibling editors have unsaved or pending changes', async () => {
    const user = userEvent.setup()
    renderWithQueryClient(
      <PublishVersionForm
        assetId={UUID_ASSET}
        blockers={['Draft metadata', 'Source declaration']}
      />,
    )

    expect(
      screen.getByText(/unsaved changes in: draft metadata, source declaration/i),
    ).toBeInTheDocument()
    const upload = screen.getByRole('button', { name: /^upload version$/i })
    expect(upload).toBeDisabled()

    await user.type(screen.getByLabelText(/^release notes$/i), 'Initial release')
    const fileInput = document.getElementById('publish-version-file') as HTMLInputElement
    await user.upload(fileInput, createArchiveFile())
    await user.click(upload)
    expect(publishSellerAssetVersion).not.toHaveBeenCalled()
  })

  it('keeps the file and explains an unconfirmed upload after a network failure', async () => {
    const user = userEvent.setup()
    publishSellerAssetVersion.mockRejectedValueOnce(new TypeError('Network failed'))
    renderWithQueryClient(<PublishVersionForm assetId={UUID_ASSET} />)
    await fillAndUpload(user)
    await waitFor(() => {
      expect(screen.getByText(/upload outcome could not be confirmed/i)).toBeInTheDocument()
    })
    expect(screen.getByText('package.zip')).toBeInTheDocument()
  })
})
