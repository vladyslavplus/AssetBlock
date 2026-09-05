import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useForm, useWatch } from 'react-hook-form'

import { ListingCopilotPanel } from '@/components/sell/listing-copilot-panel'
import type { AssetEditFormValues } from '@/lib/seller/seller-schemas'
import type { AssetProcessingJobDto } from '@/lib/seller/seller-processing-schemas'
import { renderWithProviders } from '@/test/render'

const subscribeProcessingHub = vi.hoisted(() => vi.fn())

vi.mock('@/lib/notifications/notification-hub', () => ({
  subscribeProcessingHub: (cb: (msg: unknown) => void) => subscribeProcessingHub(cb),
}))

const versionId = '33333333-3333-4333-8333-333333333333'
const suggestion = {
  jobId: '11111111-1111-4111-8111-111111111111',
  assetVersionId: versionId,
  title: 'Oak Chair',
  description: 'A wooden chair',
  category: '3D',
  tags: ['lowpoly'],
  provider: 'OPENROUTER' as const,
  actualModel: 'fixture/openrouter-test',
  modelRevision: null,
  upstreamProvider: 'TestHost',
  createdAt: '2026-08-25T12:00:00Z',
}

function copilotJob(status: AssetProcessingJobDto['status']): AssetProcessingJobDto {
  return {
    id: '11111111-1111-4111-8111-111111111111',
    assetId: '22222222-2222-4222-8222-222222222222',
    assetVersionId: versionId,
    type: 'LISTING_COPILOT',
    definitionVersion: 1,
    status,
    stage: status,
    attemptCount: 1,
    maxAttempts: 3,
    availableAt: '2026-08-25T12:00:00Z',
    startedAt: status === 'QUEUED' ? null : '2026-08-25T12:00:01Z',
    completedAt: null,
    errorCode: status === 'FAILED' ? 'ERR_AI_ERROR' : null,
    errorSummary: status === 'FAILED' ? 'The AI request failed.' : null,
    createdAt: '2026-08-25T12:00:00Z',
    updatedAt: '2026-08-25T12:00:01Z',
  }
}

function FormHost({
  stale,
  dirtyFields,
}: {
  stale?: boolean
  dirtyFields?: Partial<Readonly<Record<keyof AssetEditFormValues, boolean>>>
}) {
  const form = useForm<AssetEditFormValues>({
    defaultValues: {
      title: 'Old',
      description: 'Old desc',
      price: 1,
      categoryId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      tags: '',
    },
  })

  const title = useWatch({ control: form.control, name: 'title' })
  const description = useWatch({ control: form.control, name: 'description' })
  const categoryId = useWatch({ control: form.control, name: 'categoryId' })
  const tags = useWatch({ control: form.control, name: 'tags' })
  const price = useWatch({ control: form.control, name: 'price' })

  return (
    <div>
      <p data-testid="title">{title}</p>
      <p data-testid="description">{description}</p>
      <p data-testid="category">{categoryId}</p>
      <p data-testid="tags">{tags}</p>
      <p data-testid="price">{price}</p>
      <ListingCopilotPanel
        assetId="22222222-2222-4222-8222-222222222222"
        assetVersionId={versionId}
        categories={[{ id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', name: '3D' }]}
        catalogTags={stale ? ['other'] : ['lowpoly']}
        setValue={form.setValue}
        getValues={form.getValues}
        dirtyFields={dirtyFields}
      />
    </div>
  )
}

function mockFetch(options: {
  jobs?: AssetProcessingJobDto[]
  suggestion?: unknown
  suggestionStatus?: number
  post?: () => Promise<Response> | Response
}) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === 'POST' && options.post) {
        return options.post()
      }
      if (String(url).includes('processing-jobs')) {
        return new Response(JSON.stringify(options.jobs ?? []), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (options.suggestionStatus === 404 || options.suggestion == null) {
        return new Response('', { status: options.suggestionStatus ?? 404 })
      }
      return new Response(JSON.stringify(options.suggestion), {
        status: options.suggestionStatus ?? 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )
}

describe('ListingCopilotPanel', () => {
  beforeEach(() => {
    subscribeProcessingHub.mockImplementation(() => () => {})
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows a loading skeleton while queries are pending', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Promise<Response>(() => {})),
    )

    renderWithProviders(<FormHost />)

    expect(document.querySelectorAll('[class*="animate-pulse"]').length).toBeGreaterThan(0)
    expect(screen.queryByRole('button', { name: /generate with ai/i })).not.toBeInTheDocument()
  })

  it('shows queued, running, and retry-scheduled copy without generating again', async () => {
    mockFetch({ jobs: [copilotJob('QUEUED')] })
    const queued = renderWithProviders(<FormHost />)
    expect(await screen.findByText('Queued…')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /generate with ai/i })).not.toBeInTheDocument()
    queued.unmount()

    mockFetch({ jobs: [copilotJob('RUNNING')] })
    const running = renderWithProviders(<FormHost />)
    expect(await screen.findByText('Generating a suggestion…')).toBeInTheDocument()
    running.unmount()

    mockFetch({ jobs: [copilotJob('RETRY_SCHEDULED')] })
    renderWithProviders(<FormHost />)
    expect(await screen.findByText('Retry scheduled…')).toBeInTheDocument()
  })

  it('shows a terminal failure and retries the existing query', async () => {
    const user = userEvent.setup()
    mockFetch({ jobs: [copilotJob('FAILED')] })
    renderWithProviders(<FormHost />)

    expect(await screen.findByText('The AI request failed.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /^retry$/i }))
    await waitFor(() => expect(fetch).toHaveBeenCalled())
  })

  it('treats ERR_AI_DISABLED as unavailable and does not render provider internals', async () => {
    const user = userEvent.setup()
    mockFetch({
      jobs: [],
      post: () =>
        new Response(
          JSON.stringify({
            type: 'urn:assetblock:error:ERR_AI_DISABLED',
            title: 'Bad Request',
            status: 400,
            detail: 'internal model dump',
          }),
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
    })

    renderWithProviders(<FormHost />)

    await user.click(await screen.findByRole('button', { name: /generate with ai/i }))
    expect(
      await screen.findByText('AI listing suggestions are not available right now.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/internal model dump/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/invalid_type/i)).not.toBeInTheDocument()
  })

  it('applies only listing fields from a suggestion', async () => {
    const user = userEvent.setup()
    mockFetch({ suggestion })

    renderWithProviders(<FormHost />)

    expect(await screen.findByText('Oak Chair')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /apply suggestion/i }))
    expect(screen.getByTestId('title')).toHaveTextContent('Oak Chair')
    expect(screen.getByTestId('description')).toHaveTextContent('A wooden chair')
    expect(screen.getByTestId('category')).toHaveTextContent('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')
    expect(screen.getByTestId('tags')).toHaveTextContent('lowpoly')
    expect(screen.getByTestId('price')).toHaveTextContent('1')
  })

  it('applies only selected fields when seller unchecks specific fields', async () => {
    const user = userEvent.setup()
    mockFetch({ suggestion })

    renderWithProviders(<FormHost />)

    expect(await screen.findByText('Oak Chair')).toBeInTheDocument()

    // Uncheck title and tags
    await user.click(screen.getByRole('checkbox', { name: /select title/i }))
    await user.click(screen.getByRole('checkbox', { name: /select tags/i }))

    await user.click(screen.getByRole('button', { name: /apply suggestion/i }))

    // Title and tags should remain unchanged
    expect(screen.getByTestId('title')).toHaveTextContent('Old')
    expect(screen.getByTestId('tags')).toHaveTextContent('')

    // Description and category should be applied
    expect(screen.getByTestId('description')).toHaveTextContent('A wooden chair')
    expect(screen.getByTestId('category')).toHaveTextContent('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')
  })

  it('preserves dirty fields by default when seller has not checked overwrite', async () => {
    const user = userEvent.setup()
    mockFetch({ suggestion })

    // Title is marked as dirty
    renderWithProviders(<FormHost dirtyFields={{ title: true }} />)

    expect(await screen.findByText('Oak Chair')).toBeInTheDocument()
    expect(screen.getByText('Modified')).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /overwrite modified fields/i })).toBeInTheDocument()

    // Apply suggestion without checking overwrite
    await user.click(screen.getByRole('button', { name: /apply suggestion/i }))

    // Title is preserved
    expect(screen.getByTestId('title')).toHaveTextContent('Old')

    // Non-dirty fields are applied
    expect(screen.getByTestId('description')).toHaveTextContent('A wooden chair')
    expect(screen.getByTestId('category')).toHaveTextContent('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')
    expect(screen.getByTestId('tags')).toHaveTextContent('lowpoly')
  })

  it('overwrites dirty fields when seller explicitly checks overwrite', async () => {
    const user = userEvent.setup()
    mockFetch({ suggestion })

    // Title is marked as dirty
    renderWithProviders(<FormHost dirtyFields={{ title: true }} />)

    expect(await screen.findByText('Oak Chair')).toBeInTheDocument()
    const overwriteCheckbox = screen.getByRole('checkbox', { name: /overwrite modified fields/i })

    // Explicitly check overwrite
    await user.click(overwriteCheckbox)

    await user.click(screen.getByRole('button', { name: /apply suggestion/i }))

    // Title is now overwritten
    expect(screen.getByTestId('title')).toHaveTextContent('Oak Chair')
    expect(screen.getByTestId('description')).toHaveTextContent('A wooden chair')
    expect(screen.getByTestId('category')).toHaveTextContent('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb')
    expect(screen.getByTestId('tags')).toHaveTextContent('lowpoly')
  })

  it('disables apply when a suggested tag is missing from the catalog', async () => {
    mockFetch({ suggestion })

    renderWithProviders(<FormHost stale />)

    expect(await screen.findByText(/no longer in the catalog/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /apply suggestion/i })).toBeDisabled()
  })

  it('does not enqueue twice while pending', async () => {
    const user = userEvent.setup()
    let enqueueCalls = 0
    mockFetch({
      jobs: [],
      post: async () => {
        enqueueCalls += 1
        await new Promise(() => {})
        return new Response('{}', { status: 202 })
      },
    })

    renderWithProviders(<FormHost />)

    const generate = await screen.findByRole('button', { name: /generate with ai/i })
    await user.click(generate)
    await user.click(generate)
    await waitFor(() => expect(enqueueCalls).toBe(1))
  })
})
