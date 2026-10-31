import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SellerResponsesPanel } from './seller-responses-panel'
import { renderWithQueryClient } from '@/test/render'

const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const version = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const report = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const finding = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
const page = {
  workspaceId: id,
  workspaceRevision: 2,
  caseRevision: 1,
  evidenceRevision: 1,
  reportId: report,
  requiredCount: 1,
  outstandingCount: 1,
  evidenceText: '',
  items: [],
  totalCount: 0,
  page: 1,
  pageSize: 50,
  totalPages: 0,
}
function renderPanel() {
  return renderWithQueryClient(
    <SellerResponsesPanel
      assetId={id}
      versionId={version}
      reportId={report}
      findings={[{ id: finding, ownPath: 'own.py', responseRequired: true, hasResponse: false }]}
      canSubmit={false}
    />,
  )
}
describe('seller review responses', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })
  it.each(['network', 'server'])(
    'allows No evidence and replays uncertain %s failures',
    async (failure) => {
      const writes: unknown[] = []
      const fetchMock = vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          writes.push(JSON.parse(init.body as string))
          if (writes.length === 1) {
            if (failure === 'network') throw new TypeError('Connection lost')
            return new Response('{}', { status: 500 })
          }
          return new Response(
            JSON.stringify({
              responseId: finding,
              workspaceId: id,
              workspaceRevision: 3,
              evidenceRevision: 2,
              caseRevision: 1,
              replayed: true,
            }),
          )
        }
        return new Response(JSON.stringify(page))
      })
      vi.stubGlobal('fetch', fetchMock)
      renderPanel()
      await screen.findByText(/1 of 1 required findings/)
      fireEvent.click(screen.getByRole('button', { name: 'Save response' }))
      await screen.findByText(/Result unknown. Retry safely/)
      expect(screen.getByLabelText('Response for own.py')).toBeDisabled()
      fireEvent.click(screen.getByRole('button', { name: 'Retry same request' }))
      await screen.findByText('Saved. History retained.')
      expect(writes).toHaveLength(2)
      expect(writes[1]).toEqual(writes[0])
      expect(writes[0]).toMatchObject({
        responseType: 'NO_EVIDENCE',
        text: '',
        expectedEvidenceRevision: 1,
        expectedWorkspaceRevision: 2,
      })
      expect(screen.queryByRole('button', { name: 'Submit for review' })).not.toBeInTheDocument()
    },
  )
  it('shows explicit evidence conflicts without silently retrying changed expectations', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'PUT'
          ? new Response(
              JSON.stringify({
                code: 'ERR_SELLER_EVIDENCE_STALE',
                detail: 'Review evidence changed. Refresh this version and try again.',
              }),
              { status: 409 },
            )
          : new Response(JSON.stringify(page)),
      ),
    )
    renderPanel()
    await screen.findByText(/1 of 1 required findings/)
    fireEvent.change(screen.getByLabelText('Additional review evidence'), {
      target: { value: 'Correction' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save evidence notes' }))
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/Review evidence changed/),
    )
  })
  it('shows a local precondition error without suggesting uncertain transport replay', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(page))),
    )
    vi.spyOn(crypto, 'randomUUID').mockImplementation(() => {
      throw new Error('No current report is available.')
    })
    renderPanel()
    await screen.findByText(/1 of 1 required findings/)
    fireEvent.click(screen.getByRole('button', { name: 'Save response' }))
    await screen.findByText('No current report is available.')
    expect(screen.queryByText(/Result unknown/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Response for own.py')).not.toBeDisabled()
  })

  it('adopts clean refetched notes and preserves dirty text and its original CAS basis', async () => {
    let latestPage = page
    const writes: Array<Record<string, unknown>> = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'PUT') {
          writes.push(JSON.parse(init.body as string))
          return new Response(
            JSON.stringify({
              code: 'ERR_SELLER_EVIDENCE_STALE',
              detail: 'Review evidence changed.',
            }),
            { status: 409 },
          )
        }
        return new Response(JSON.stringify(latestPage))
      }),
    )
    const { queryClient } = renderPanel()
    await screen.findByText(/1 of 1 required findings/)
    const { sellerKeys } = await import('@/lib/seller/seller-query')
    latestPage = {
      ...page,
      evidenceText: 'Saved elsewhere',
      evidenceRevision: 2,
      workspaceRevision: 3,
    }
    queryClient.setQueryData([...sellerKeys.responses(id, version), 1], latestPage)
    await waitFor(() =>
      expect(screen.getByLabelText('Additional review evidence')).toHaveValue('Saved elsewhere'),
    )
    fireEvent.change(screen.getByLabelText('Additional review evidence'), {
      target: { value: 'Unsaved local notes' },
    })
    latestPage = {
      ...page,
      evidenceText: 'New server notes',
      evidenceRevision: 3,
      workspaceRevision: 4,
    }
    queryClient.setQueryData([...sellerKeys.responses(id, version), 1], latestPage)
    await waitFor(() =>
      expect(screen.getByLabelText('Additional review evidence')).toHaveValue(
        'Unsaved local notes',
      ),
    )
    fireEvent.click(screen.getByRole('button', { name: 'Save evidence notes' }))
    await screen.findByText('Review evidence changed.')
    expect(writes[0]).toMatchObject({
      text: 'Unsaved local notes',
      expectedWorkspaceRevision: 3,
      expectedEvidenceRevision: 2,
    })
    fireEvent.click(screen.getByRole('button', { name: 'Use latest saved evidence' }))
    expect(screen.getByLabelText('Additional review evidence')).toHaveValue('New server notes')
  })
})
