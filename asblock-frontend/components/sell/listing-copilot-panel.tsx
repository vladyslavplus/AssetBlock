'use client'

import { Sparkles, Loader2, AlertCircle } from 'lucide-react'
import type { UseFormSetValue } from 'react-hook-form'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { ListingCopilotRequestError } from '@/lib/seller/seller-copilot-api'
import {
  useEnqueueListingCopilotMutation,
  useListingCopilotSuggestionQuery,
} from '@/lib/seller/seller-copilot-query'
import type { AssetEditFormValues } from '@/lib/seller/seller-schemas'
import { useAssetVersionProcessingJobsQuery } from '@/lib/seller/seller-processing-query'
import { isNonTerminalStatus } from '@/lib/seller/seller-processing-schemas'
import { runQueryInBackground } from '@/lib/query/query-refresh'

interface ListingCopilotPanelProps {
  assetId: string
  assetVersionId: string | undefined
  categories: Array<{ id: string; name: string }>
  catalogTags: string[]
  setValue: UseFormSetValue<AssetEditFormValues>
}

export function ListingCopilotPanel({
  assetId,
  assetVersionId,
  categories,
  catalogTags,
  setValue,
}: ListingCopilotPanelProps) {
  const jobsQuery = useAssetVersionProcessingJobsQuery(assetVersionId)
  const suggestionQuery = useListingCopilotSuggestionQuery(assetVersionId)
  const enqueue = useEnqueueListingCopilotMutation(assetId, assetVersionId)

  const copilotJob = (jobsQuery.data ?? []).find((job) => job.type === 'LISTING_COPILOT')
  const suggestion = suggestionQuery.data ?? null
  const disabled =
    enqueue.error instanceof ListingCopilotRequestError && enqueue.error.code === 'AI_DISABLED'
  const jobPending = copilotJob != null && isNonTerminalStatus(copilotJob.status)
  const jobFailed = copilotJob?.status === 'FAILED' || copilotJob?.status === 'CANCELLED'
  const pendingLabel =
    copilotJob?.status === 'QUEUED'
      ? 'Queued…'
      : copilotJob?.status === 'RETRY_SCHEDULED'
        ? 'Retry scheduled…'
        : 'Generating a suggestion…'
  const staleCategory =
    suggestion != null && !categories.some((c) => c.name === suggestion.category)
  const staleTags =
    suggestion != null && suggestion.tags.some((tag) => !catalogTags.some((name) => name === tag))
  const staleTaxonomy = staleCategory || staleTags
  const canApply = suggestion != null && !staleTaxonomy

  if (!assetVersionId) {
    return null
  }

  if (jobsQuery.isLoading || suggestionQuery.isLoading) {
    return (
      <div className="space-y-2 rounded-md border border-border p-3">
        <Skeleton className="h-4 w-40 bg-muted-foreground/20 animate-pulse motion-reduce:animate-none" />
        <Skeleton className="h-16 w-full bg-muted-foreground/20 animate-pulse motion-reduce:animate-none" />
      </div>
    )
  }

  return (
    <div className="space-y-3 rounded-md border border-border p-3">
      <div className="flex items-center gap-2 text-sm font-medium">
        <Sparkles className="h-4 w-4" aria-hidden />
        AI listing suggestion
      </div>

      {disabled ? (
        <p className="text-xs text-muted-foreground">
          AI listing suggestions are not available right now.
        </p>
      ) : null}

      {jobsQuery.isError || suggestionQuery.isError ? (
        <Alert variant="destructive" role="alert" className="py-2">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription className="text-xs">
            Could not load AI listing status.
            <Button
              type="button"
              variant="ghost"
              className="h-auto px-1 text-xs"
              onClick={() => {
                runQueryInBackground(jobsQuery.refetch())
                runQueryInBackground(suggestionQuery.refetch())
              }}
            >
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}

      {jobPending ? (
        <p className="text-xs text-muted-foreground flex items-center gap-2">
          <Loader2 className="h-3.5 w-3.5 animate-spin motion-reduce:animate-none" aria-hidden />
          {pendingLabel}
        </p>
      ) : null}

      {jobFailed && !suggestion ? (
        <Alert variant="destructive" role="alert" className="py-2">
          <AlertCircle className="h-4 w-4" />
          <AlertDescription className="text-xs">
            {copilotJob.errorSummary ?? 'AI listing suggestion failed.'}
            <Button
              type="button"
              variant="ghost"
              className="h-auto px-1 text-xs"
              onClick={() => runQueryInBackground(jobsQuery.refetch())}
            >
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}

      {suggestion ? (
        <div className="space-y-2 text-sm">
          <p className="font-medium">{suggestion.title}</p>
          <p className="text-xs text-muted-foreground whitespace-pre-wrap">
            {suggestion.description}
          </p>
          <p className="text-xs">Category: {suggestion.category}</p>
          <p className="text-xs">
            Tags: {suggestion.tags.length > 0 ? suggestion.tags.join(', ') : 'None'}
          </p>
          <p className="text-[11px] text-muted-foreground">AI-generated — review before saving</p>
          {staleTaxonomy ? (
            <p className="text-xs text-amber-800 dark:text-amber-200">
              This suggestion uses category or tag names that are no longer in the catalog. Apply is
              disabled.
            </p>
          ) : null}
          <Button
            type="button"
            variant="secondary"
            disabled={!canApply}
            onClick={() => {
              const categoryId = categories.find((c) => c.name === suggestion.category)?.id
              if (!categoryId) {
                return
              }
              setValue('title', suggestion.title, { shouldDirty: true, shouldValidate: true })
              setValue('description', suggestion.description, {
                shouldDirty: true,
                shouldValidate: true,
              })
              setValue('categoryId', categoryId, { shouldDirty: true, shouldValidate: true })
              setValue('tags', suggestion.tags.join(', '), {
                shouldDirty: true,
                shouldValidate: true,
              })
            }}
          >
            Apply suggestion
          </Button>
        </div>
      ) : null}

      {!disabled && !suggestion && !jobPending && !jobFailed ? (
        <Button
          type="button"
          disabled={enqueue.isPending}
          onClick={() => {
            enqueue.reset()
            enqueue.mutate()
          }}
        >
          {enqueue.isPending ? (
            <>
              <Loader2
                className="h-4 w-4 mr-2 animate-spin motion-reduce:animate-none"
                aria-hidden
              />
              Starting…
            </>
          ) : (
            'Generate with AI'
          )}
        </Button>
      ) : null}

      {enqueue.isError && !disabled ? (
        <p className="text-xs text-destructive" role="alert">
          {enqueue.error.message}
        </p>
      ) : null}
    </div>
  )
}
