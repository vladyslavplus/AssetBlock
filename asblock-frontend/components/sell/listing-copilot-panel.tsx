'use client'

import { useState } from 'react'
import { Sparkles, Loader2, AlertCircle } from 'lucide-react'
import type { UseFormGetValues, UseFormSetValue } from 'react-hook-form'
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

export type CopilotFieldKey = 'title' | 'description' | 'category' | 'tags'

interface ListingCopilotPanelProps {
  assetId: string
  assetVersionId: string | undefined
  categories: Array<{ id: string; name: string }>
  catalogTags: string[]
  setValue: UseFormSetValue<AssetEditFormValues>
  getValues?: UseFormGetValues<AssetEditFormValues>
  dirtyFields?: Partial<Readonly<Record<keyof AssetEditFormValues, boolean>>>
}

export function ListingCopilotPanel({
  assetId,
  assetVersionId,
  categories,
  catalogTags,
  setValue,
  getValues,
  dirtyFields,
}: ListingCopilotPanelProps) {
  const [selectedFields, setSelectedFields] = useState<Record<CopilotFieldKey, boolean>>({
    title: true,
    description: true,
    category: true,
    tags: true,
  })
  const [overwriteDirty, setOverwriteDirty] = useState<boolean>(false)

  const jobsQuery = useAssetVersionProcessingJobsQuery(assetVersionId)
  const suggestionQuery = useListingCopilotSuggestionQuery(assetVersionId)
  const enqueue = useEnqueueListingCopilotMutation(assetId, assetVersionId)

  const copilotJob = (jobsQuery.data ?? []).find((job) => job.type === 'LISTING_COPILOT')
  const suggestion = suggestionQuery.data ?? null
  const disabled =
    enqueue.error instanceof ListingCopilotRequestError && enqueue.error.code === 'ERR_AI_DISABLED'
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

  const isFieldDirty = (key: CopilotFieldKey): boolean => {
    if (!dirtyFields) return false
    if (key === 'category') return Boolean(dirtyFields.categoryId)
    return Boolean(dirtyFields[key])
  }

  const hasSelectedFields = Object.values(selectedFields).some(Boolean)
  const hasDirtySelectedField = (Object.keys(selectedFields) as CopilotFieldKey[]).some(
    (key) => selectedFields[key] && isFieldDirty(key),
  )
  const canApply = suggestion != null && !staleTaxonomy && hasSelectedFields

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

  const currentCategoryName = categories.find((c) => c.id === getValues?.('categoryId'))?.name
  const fieldsConfig: Array<{
    key: CopilotFieldKey
    label: string
    current: string
    suggested: string
  }> = [
    {
      key: 'title',
      label: 'Title',
      current: getValues?.('title') ?? '',
      suggested: suggestion?.title ?? '',
    },
    {
      key: 'description',
      label: 'Description',
      current: getValues?.('description') ?? '',
      suggested: suggestion?.description ?? '',
    },
    {
      key: 'category',
      label: 'Category',
      current: currentCategoryName ?? 'None',
      suggested: suggestion?.category ?? '',
    },
    {
      key: 'tags',
      label: 'Tags',
      current: getValues?.('tags') ?? '',
      suggested: suggestion?.tags.length ? suggestion.tags.join(', ') : 'None',
    },
  ]

  const handleApply = () => {
    if (!suggestion || !canApply) {
      return
    }

    const shouldApplyField = (key: CopilotFieldKey) => {
      if (!selectedFields[key]) return false
      if (isFieldDirty(key) && !overwriteDirty) {
        return false
      }
      return true
    }

    if (shouldApplyField('title')) {
      setValue('title', suggestion.title, { shouldDirty: true, shouldValidate: true })
    }
    if (shouldApplyField('description')) {
      setValue('description', suggestion.description, {
        shouldDirty: true,
        shouldValidate: true,
      })
    }
    if (shouldApplyField('category')) {
      const categoryId = categories.find((c) => c.name === suggestion.category)?.id
      if (categoryId) {
        setValue('categoryId', categoryId, { shouldDirty: true, shouldValidate: true })
      }
    }
    if (shouldApplyField('tags')) {
      setValue('tags', suggestion.tags.join(', '), {
        shouldDirty: true,
        shouldValidate: true,
      })
    }
  }

  return (
    <div className="space-y-3 rounded-md border border-border p-3">
      <div className="flex items-center gap-2 text-sm font-medium">
        <Sparkles className="h-4 w-4" aria-hidden />
        AI listing suggestion
      </div>

      <p className="text-[11px] text-muted-foreground">
        Archive metadata and a sanitized README excerpt are processed by AI to generate suggestions.
        README content is sent to remote AI providers only when zero-data-retention is enabled.
      </p>

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
        <div className="space-y-3 text-sm">
          <div className="space-y-2">
            <p className="text-xs font-semibold text-foreground/80">
              Review and select suggested fields to apply:
            </p>
            {fieldsConfig.map((field) => {
              const dirty = isFieldDirty(field.key)
              return (
                <div
                  key={field.key}
                  className="p-2.5 rounded-md border border-border bg-card-elevated space-y-1.5 text-xs"
                >
                  <div className="flex items-center justify-between">
                    <label className="flex items-center gap-2 font-medium cursor-pointer">
                      <input
                        type="checkbox"
                        aria-label={`Select ${field.label}`}
                        checked={selectedFields[field.key]}
                        onChange={(e) =>
                          setSelectedFields((prev) => ({
                            ...prev,
                            [field.key]: e.target.checked,
                          }))
                        }
                        className="rounded border-input text-primary focus:ring-primary"
                      />
                      <span>{field.label}</span>
                      {dirty ? (
                        <span className="text-[10px] bg-amber-500/20 text-amber-700 dark:text-amber-300 px-1.5 py-0.2 rounded">
                          Modified
                        </span>
                      ) : null}
                    </label>
                  </div>
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-2 text-muted-foreground pt-1 border-t border-border/50">
                    <div>
                      <span className="font-semibold block text-[11px] text-foreground/70">
                        Current:
                      </span>
                      <span className="break-words line-clamp-2">{field.current || '(empty)'}</span>
                    </div>
                    <div>
                      <span className="font-semibold block text-[11px] text-foreground/70">
                        Suggested:
                      </span>
                      <span className="text-foreground break-words line-clamp-2">
                        {field.suggested || '(empty)'}
                      </span>
                    </div>
                  </div>
                </div>
              )
            })}
          </div>

          {hasDirtySelectedField ? (
            <div className="p-2.5 rounded border border-amber-500/40 bg-amber-500/10 text-xs text-amber-900 dark:text-amber-200 space-y-1">
              <label className="flex items-center gap-2 cursor-pointer font-medium">
                <input
                  type="checkbox"
                  aria-label="Overwrite modified fields"
                  checked={overwriteDirty}
                  onChange={(e) => setOverwriteDirty(e.target.checked)}
                  className="rounded border-input text-primary focus:ring-primary"
                />
                <span>Overwrite modified fields</span>
              </label>
              <p className="text-[11px] text-muted-foreground ml-5">
                One or more selected fields have unsaved edits. By default, modified fields are
                preserved.
              </p>
            </div>
          ) : null}

          <p className="text-[11px] text-muted-foreground">AI-generated — review before saving</p>
          {staleTaxonomy ? (
            <p className="text-xs text-amber-800 dark:text-amber-200">
              This suggestion uses category or tag names that are no longer in the catalog. Apply is
              disabled.
            </p>
          ) : null}
          <Button type="button" variant="secondary" disabled={!canApply} onClick={handleApply}>
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
