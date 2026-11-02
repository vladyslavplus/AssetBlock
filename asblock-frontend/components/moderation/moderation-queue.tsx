'use client'

import { useState } from 'react'
import Link from 'next/link'
import type { Route } from 'next'
import { useAuth } from '@/components/auth/auth-context'
import { Button } from '@/components/ui/button'
import { QueryEmptyState } from '@/components/shared/query-empty-state'
import {
  ModerationApiError,
  useChangeModerationClaim,
  useModerationQueue,
} from '@/lib/moderation/moderation-query'
import type { ModeratorQueueItem } from '@/lib/moderation/moderation-schemas'

function claimErrorMessage(error: Error | null, action?: 'claim' | 'release') {
  if (!(error instanceof ModerationApiError)) return error?.message ?? null
  if (error.status === 409 && action) {
    return action === 'claim'
      ? 'Claim state changed. Refresh the queue before trying again.'
      : 'Release state changed. Refresh the queue before trying again.'
  }
  if (error.status === 409)
    return 'Case changed or another reviewer holds it. Queue refreshed; review the current state before trying again.'
  if (error.status === 403) return 'You are not permitted to claim or release this case.'
  return error.message
}

function claimHolderLabel(item: ModeratorQueueItem, userId: string | undefined) {
  if (!item.claimHolderId) return 'Unclaimed'
  if (item.claimHolderId === userId) return 'You'
  return item.claimHolderName ?? 'Another reviewer'
}

export function ModerationQueue() {
  const { user } = useAuth()
  const [claimFilter, setClaimFilter] = useState<'ALL' | 'MINE' | 'UNCLAIMED'>('ALL')
  const queue = useModerationQueue(user?.id, { claimFilter, pageSize: 50 })
  const claimMutation = useChangeModerationClaim()
  const mutationError = claimErrorMessage(claimMutation.error, claimMutation.variables?.action)

  async function changeClaim(item: ModeratorQueueItem, action: 'claim' | 'release') {
    claimMutation.reset()
    try {
      await claimMutation.mutateAsync({
        submissionId: item.submissionId,
        action,
        body: { operationId: crypto.randomUUID(), expectedCaseRevision: item.caseRevision },
      })
    } catch (error) {
      if (error instanceof ModerationApiError && error.status === 409) {
        await queue.refetch()
      }
    }
  }

  if (queue.isPending) {
    return (
      <p role="status" className="text-sm text-muted-foreground">
        Loading moderation queue…
      </p>
    )
  }
  if (queue.isError) {
    return (
      <section aria-labelledby="moderation-queue-error" className="space-y-3">
        <h2 id="moderation-queue-error" className="text-lg font-semibold">
          Could not load the queue
        </h2>
        <p role="alert" className="text-sm text-destructive">
          {queue.error.message}
        </p>
        <Button type="button" onClick={() => void queue.refetch()}>
          Try again
        </Button>
      </section>
    )
  }

  const items = queue.data.pages.flatMap((page) => page.items)
  return (
    <section aria-labelledby="moderation-queue-heading" className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="moderation-queue-heading" className="text-xl font-semibold">
          Review queue
        </h2>
        <label className="flex items-center gap-2 text-sm">
          <span>Show</span>
          <select
            aria-label="Filter queue by claim"
            className="rounded-md border border-input bg-background px-3 py-2"
            value={claimFilter}
            onChange={(event) => setClaimFilter(event.target.value as typeof claimFilter)}
          >
            <option value="ALL">All cases</option>
            <option value="MINE">Claimed by me</option>
            <option value="UNCLAIMED">Available cases</option>
          </select>
        </label>
      </div>

      {mutationError ? (
        <p role="alert" className="rounded-md border border-destructive/40 p-3 text-sm">
          {mutationError}
        </p>
      ) : null}

      {items.length === 0 ? (
        <QueryEmptyState
          title="No cases in this queue"
          description="New eligible submissions will appear here."
          headingLevel="h3"
        />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-border">
          <table className="w-full min-w-[52rem] text-left text-sm">
            <caption className="sr-only">Moderation cases available to review</caption>
            <thead className="bg-muted/40 text-muted-foreground">
              <tr>
                <th scope="col" className="px-4 py-3">
                  Asset
                </th>
                <th scope="col" className="px-4 py-3">
                  Submitted
                </th>
                <th scope="col" className="px-4 py-3">
                  State
                </th>
                <th scope="col" className="px-4 py-3">
                  Coverage
                </th>
                <th scope="col" className="px-4 py-3">
                  Findings
                </th>
                <th scope="col" className="px-4 py-3">
                  Claim holder
                </th>
                <th scope="col" className="px-4 py-3">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {items.map((item) => {
                const mine = item.claimHolderId === user?.id
                const action = mine ? 'release' : 'claim'
                const busy =
                  claimMutation.isPending &&
                  claimMutation.variables?.submissionId === item.submissionId
                return (
                  <tr key={item.submissionId}>
                    <th scope="row" className="px-4 py-3 font-medium text-foreground">
                      <span className="flex flex-wrap items-center gap-2">
                        <Link
                          className="underline"
                          href={`/moderation/cases/${item.submissionId}` as Route}
                        >
                          {item.assetTitle}
                        </Link>
                        {item.kind === 'APPEAL' ? (
                          <span className="rounded-full border border-primary/40 px-2 py-0.5 text-xs text-primary">
                            Appeal
                          </span>
                        ) : null}
                      </span>
                      <span className="block text-xs font-normal text-muted-foreground">
                        Version {item.versionNumber}
                      </span>
                    </th>
                    <td className="px-4 py-3">{new Date(item.submittedAt).toLocaleString()}</td>
                    <td className="px-4 py-3">{item.state.replaceAll('_', ' ')}</td>
                    <td className="px-4 py-3">{item.coverageLevel ?? 'Unavailable'}</td>
                    <td className="px-4 py-3">
                      {item.materialFindingCount} material
                      {item.materialFindingCount === 1 ? '' : ' findings'}
                      {item.hasOpenResponses ? (
                        <span className="block text-xs text-amber-700">Responses outstanding</span>
                      ) : null}
                    </td>
                    <td className="px-4 py-3">{claimHolderLabel(item, user?.id)}</td>
                    <td className="px-4 py-3">
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={busy || (item.claimHolderId !== null && !mine)}
                        onClick={() => void changeClaim(item, action)}
                        aria-label={`${mine ? 'Release' : 'Claim'} ${item.assetTitle}`}
                      >
                        {busy ? 'Saving…' : mine ? 'Release' : 'Claim'}
                      </Button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {queue.hasNextPage ? (
        <Button
          type="button"
          variant="outline"
          disabled={queue.isFetchingNextPage}
          onClick={() => void queue.fetchNextPage()}
        >
          {queue.isFetchingNextPage ? 'Loading more…' : 'Load more'}
        </Button>
      ) : null}
      {queue.isFetchNextPageError ? (
        <p role="alert" className="text-sm text-destructive">
          Could not load more cases. Try again.
        </p>
      ) : null}
    </section>
  )
}
