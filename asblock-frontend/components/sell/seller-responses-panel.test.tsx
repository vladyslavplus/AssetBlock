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
function renderPanel(canSubmit = false) {
  return renderWithQueryClient(
    <SellerResponsesPanel
      assetId={id}
      versionId={version}
      reportId={report}
      findings={[{ id: finding, ownPath: 'own.py', responseRequired: true, hasResponse: false }]}
      canSubmit={canSubmit}
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
      expect(screen.getByRole('button', { name: 'Submit for review' })).toBeDisabled()
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

  it('shows the backend response-required message and blocks submission while a required finding is unanswered', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(page))),
    )
    renderPanel()

    await screen.findByText(/1 of 1 required findings/)

    expect(screen.getByRole('button', { name: 'Submit for review' })).toBeDisabled()
    expect(screen.getByText(/Response for own.py - Required response/)).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent(
      'Respond to every required finding, or choose No evidence, before submitting this version.',
    )
  })

  it('sends only one submission when the seller double-clicks', async () => {
    let finish: ((response: Response) => void) | undefined
    const writes: unknown[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          writes.push(JSON.parse(init.body as string))
          return new Promise<Response>((resolve) => {
            finish = resolve
          })
        }
        return new Response(JSON.stringify({ ...page, outstandingCount: 0, requiredCount: 0 }))
      }),
    )
    renderWithQueryClient(
      <SellerResponsesPanel
        assetId={id}
        versionId={version}
        reportId={report}
        findings={[]}
        canSubmit
      />,
    )
    await screen.findByRole('button', { name: 'Submit for review' })
    fireEvent.click(screen.getByRole('button', { name: 'Submit for review' }))
    fireEvent.click(screen.getByRole('button', { name: 'Submit for review' }))
    await waitFor(() => expect(writes).toHaveLength(1))
    finish?.(new Response(JSON.stringify({ submissionId: finding })))
    await screen.findByText('Submitted for human review.')
  })

  it('reuses the unchanged submit operation after an uncertain response and refreshed revisions', async () => {
    const writes: Array<Record<string, unknown>> = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          writes.push(JSON.parse(init.body as string))
          if (writes.length === 1) throw new TypeError('Connection lost')
          return new Response(JSON.stringify({ submissionId: finding }))
        }
        return new Response(
          JSON.stringify(
            writes.length
              ? { ...page, workspaceRevision: 3, caseRevision: 2, outstandingCount: 0 }
              : { ...page, outstandingCount: 0 },
          ),
        )
      }),
    )
    renderPanel(true)
    fireEvent.click(await screen.findByRole('button', { name: 'Submit for review' }))
    await screen.findByRole('button', { name: 'Retry submit for review' })
    fireEvent.click(screen.getByRole('button', { name: 'Retry submit for review' }))
    await screen.findByText('Submitted for human review.')

    expect(writes).toHaveLength(2)
    expect(writes[1]).toEqual(writes[0])
    expect(writes[0]).toMatchObject({
      expectedWorkspaceRevision: page.workspaceRevision,
      expectedCaseRevision: page.caseRevision,
    })
  })

  it('preserves an unsaved finding explanation when submission receives a stale 409', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'POST'
          ? new Response(
              JSON.stringify({
                code: 'ERR_MODERATION_WORKSPACE_STALE',
                detail: 'The draft changed. Refresh version review before submitting.',
              }),
              { status: 409 },
            )
          : new Response(JSON.stringify({ ...page, outstandingCount: 0 })),
      ),
    )
    renderPanel(true)
    const explanation = await screen.findByRole('textbox', { name: 'Response for own.py' })
    fireEvent.change(explanation, { target: { value: 'I authored this source independently.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Submit for review' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/draft changed/i)
    expect(explanation).toHaveValue('I authored this source independently.')
  })

  it('shows append-only response history as seller text without actor identities', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              ...page,
              outstandingCount: 0,
              items: [
                {
                  id: finding,
                  assetVersionId: version,
                  reportId: report,
                  findingId: finding,
                  evidenceRevision: 1,
                  responseType: 'NO_EVIDENCE',
                  text: '',
                  createdAt: '2026-10-31T10:00:00Z',
                  actorUserId: '88888888-8888-4888-8888-888888888888',
                  isCurrent: false,
                },
              ],
              totalCount: 1,
            }),
          ),
      ),
    )
    renderPanel()

    fireEvent.click(await screen.findByText('Response history (1)'))
    expect(
      await screen.findByText('NO EVIDENCE - evidence revision 1 - Historical answer'),
    ).toBeInTheDocument()
    expect(screen.getByText('No evidence supplied.')).toBeInTheDocument()
    expect(screen.queryByText('88888888-8888-4888-8888-888888888888')).not.toBeInTheDocument()
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
