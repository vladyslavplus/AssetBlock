import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AssetProcessingStatusPanel } from '@/components/sell/asset-processing-status-panel'
import type { AssetProcessingJobDto } from '@/lib/seller/seller-processing-schemas'
import { createTestQueryClient } from '@/test/query-client'

const subscribeProcessingHub = vi.hoisted(() => vi.fn())

vi.mock('@/lib/notifications/notification-hub', () => ({
  subscribeProcessingHub: (cb: (msg: unknown) => void, userId: string) =>
    subscribeProcessingHub(cb, userId),
}))

const activeJob: AssetProcessingJobDto = {
  id: '11111111-1111-4111-8111-111111111111',
  assetId: '22222222-2222-4222-8222-222222222222',
  assetVersionId: '33333333-3333-4333-8333-333333333333',
  type: 'ARCHIVE_INSPECTION',
  definitionVersion: 1,
  status: 'RUNNING',
  stage: 'INSPECTING',
  attemptCount: 1,
  maxAttempts: 3,
  availableAt: '2026-08-24T12:00:00Z',
  startedAt: '2026-08-24T12:00:01Z',
  completedAt: null,
  errorCode: null,
  errorSummary: null,
  createdAt: '2026-08-24T12:00:00Z',
  updatedAt: '2026-08-24T12:00:01Z',
}

const failedJob: AssetProcessingJobDto = {
  id: '44444444-4444-4444-8444-444444444444',
  assetId: '22222222-2222-4222-8222-222222222222',
  assetVersionId: '33333333-3333-4333-8333-333333333333',
  type: 'MALWARE_SCAN',
  definitionVersion: 1,
  status: 'FAILED',
  stage: 'INFECTED',
  attemptCount: 2,
  maxAttempts: 3,
  availableAt: '2026-08-24T12:00:00Z',
  startedAt: '2026-08-24T12:00:01Z',
  completedAt: '2026-08-24T12:00:05Z',
  errorCode: 'MALWARE_DETECTED',
  errorSummary: 'Malicious signature detected in binary archive.',
  createdAt: '2026-08-24T12:00:00Z',
  updatedAt: '2026-08-24T12:00:05Z',
}

const succeededJob: AssetProcessingJobDto = {
  id: '55555555-5555-4555-8555-555555555555',
  assetId: '22222222-2222-4222-8222-222222222222',
  assetVersionId: '33333333-3333-4333-8333-333333333333',
  type: 'LISTING_COPILOT',
  definitionVersion: 1,
  status: 'SUCCEEDED',
  stage: 'COMPLETED',
  attemptCount: 1,
  maxAttempts: 3,
  availableAt: '2026-08-24T12:00:00Z',
  startedAt: '2026-08-24T12:00:01Z',
  completedAt: '2026-08-24T12:00:03Z',
  errorCode: null,
  errorSummary: null,
  createdAt: '2026-08-24T12:00:00Z',
  updatedAt: '2026-08-24T12:00:03Z',
}

describe('AssetProcessingStatusPanel', () => {
  beforeEach(() => {
    subscribeProcessingHub.mockReset()
  })

  it('renders loading skeleton while query is pending', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise(() => {})), // never resolves
    )

    const { container } = render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId="22222222-2222-4222-8222-222222222222" />
      </QueryClientProvider>,
    )

    expect(container.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0)
  })

  it('renders compact error state with Retry button on failure', async () => {
    const user = userEvent.setup()
    let calls = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        calls += 1
        if (calls === 1) {
          return new Response('{"detail":"Network failure"}', { status: 500 })
        }
        return new Response(JSON.stringify([activeJob]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId="22222222-2222-4222-8222-222222222222" />
      </QueryClientProvider>,
    )

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByText(/Network failure/i)).toBeInTheDocument()

    const retryBtn = screen.getByRole('button', { name: /retry/i })
    await user.click(retryBtn)

    expect(await screen.findByText('Archive Inspection')).toBeInTheDocument()
  })

  it('renders nothing when jobs list is empty', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    const { container } = render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId="22222222-2222-4222-8222-222222222222" />
      </QueryClientProvider>,
    )

    await waitFor(() => {
      expect(container.firstChild).toBeNull()
    })
  })

  it('renders active running jobs with stage indicator', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([activeJob]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId="22222222-2222-4222-8222-222222222222" />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Archive Inspection')).toBeInTheDocument()
    expect(screen.getByText('Processing')).toBeInTheDocument()
    expect(screen.getByText('INSPECTING')).toBeInTheDocument()
  })

  it('renders failed job with safe error summary alert', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([failedJob]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId="22222222-2222-4222-8222-222222222222" />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Malware & Security Scan')).toBeInTheDocument()
    expect(screen.getByText('Failed')).toBeInTheDocument()
    expect(screen.getByText('Malicious signature detected in binary archive.')).toBeInTheDocument()
    expect(screen.getByText(/Attempt 2 of 3/)).toBeInTheDocument()
  })

  it('renders succeeded job with Passed badge', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([succeededJob]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId="22222222-2222-4222-8222-222222222222" />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('AI Listing Analysis')).toBeInTheDocument()
    expect(screen.getByText('Passed')).toBeInTheDocument()
  })

  it('does not subscribe to SignalR; that belongs to the authenticated shell', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([activeJob]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel
          assetId="22222222-2222-4222-8222-222222222222"
          assetVersionId="33333333-3333-4333-8333-333333333333"
        />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Archive Inspection')).toBeInTheDocument()
    expect(subscribeProcessingHub).not.toHaveBeenCalled()
  })

  it('renders distinct version labels when versions metadata is provided', async () => {
    const v1Job: AssetProcessingJobDto = {
      ...activeJob,
      id: '11111111-1111-4111-8111-111111111111',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
    }
    const v2Job: AssetProcessingJobDto = {
      ...activeJob,
      id: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '44444444-4444-4444-8444-444444444444',
    }

    const versions = [
      {
        id: '33333333-3333-4333-8333-333333333333',
        versionNumber: 1,
        contentLength: 1000,
        contentSha256: 'a'.repeat(64),
        fileName: 'v1.zip',
        isCurrent: false,
        releaseNotes: null,
        license: {
          code: 'PERSONAL' as const,
          displayName: 'Personal',
          templateVersion: '1.0',
          terms: '',
        },
        createdAt: '2026-08-24T12:00:00Z',
        processingStatus: 'READY' as const,
        processingErrorCode: null,
        processingErrorSummary: null,
        processingUpdatedAt: null,
      },
      {
        id: '44444444-4444-4444-8444-444444444444',
        versionNumber: 2,
        contentLength: 2000,
        contentSha256: 'b'.repeat(64),
        fileName: 'v2.zip',
        isCurrent: true,
        releaseNotes: null,
        license: {
          code: 'PERSONAL' as const,
          displayName: 'Personal',
          templateVersion: '1.0',
          terms: '',
        },
        createdAt: '2026-08-24T12:00:00Z',
        processingStatus: 'READY' as const,
        processingErrorCode: null,
        processingErrorSummary: null,
        processingUpdatedAt: null,
      },
    ]

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([v1Job, v2Job]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId={activeJob.assetId} versions={versions} />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('v1')).toBeInTheDocument()
    expect(screen.getByText('v2')).toBeInTheDocument()
  })

  it('renders generic safe error without raw Zod details when API response is malformed', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([{ invalid: 'shape', missing: 'fields' }]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId={activeJob.assetId} />
      </QueryClientProvider>,
    )

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByText(/Could not load processing/i)).toBeInTheDocument()
    expect(screen.queryByText(/ZodError|issues|invalid_type/)).not.toBeInTheDocument()
  })

  it('renders distinct fallback labels when versions metadata is missing or unmatched', async () => {
    const v1Job: AssetProcessingJobDto = {
      ...activeJob,
      id: '11111111-1111-4111-8111-111111111111',
      assetVersionId: '33333333-3333-4333-8333-333333333333',
    }
    const v2Job: AssetProcessingJobDto = {
      ...activeJob,
      id: '22222222-2222-4222-8222-222222222222',
      assetVersionId: '44444444-4444-4444-8444-444444444444',
    }

    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify([v1Job, v2Job]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    render(
      <QueryClientProvider client={createTestQueryClient()}>
        <AssetProcessingStatusPanel assetId={activeJob.assetId} />
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Version …333333')).toBeInTheDocument()
    expect(screen.getByText('Version …444444')).toBeInTheDocument()
  })
})
