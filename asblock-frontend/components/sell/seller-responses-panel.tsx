'use client'

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState } from 'react'
import {
  fetchSellerResponses,
  saveSellerEvidence,
  saveSellerResponse,
  submitSellerAssetVersion,
} from '@/lib/seller/seller-draft-api'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { toast } from 'sonner'
import { ApiRequestError } from '@/lib/http/api-client'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { Field, FieldLabel, FieldError } from '@/components/ui/field'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { sellerKeys } from '@/lib/seller/seller-query'
import {
  SELLER_RESPONSE_TEXT_MAX,
  sellerResponseFormSchema,
  type SellerResponseFormValues,
  sellerResponseTypeSchema,
  type SellerResponseRequest,
  type SellerEvidenceEditRequest,
  type SellerResponsesPage,
} from '@/lib/seller/seller-draft-schemas'

interface Finding {
  id: string
  ownPath: string
  responseRequired: boolean
  hasResponse: boolean
}
interface Props {
  assetId: string
  versionId: string
  reportId: string
  findings: Finding[]
  canSubmit: boolean
}

export function SellerResponsesPanel({ assetId, versionId, reportId, findings, canSubmit }: Props) {
  const client = useQueryClient()
  const [page, setPage] = useState(1)
  const query = useQuery({
    queryKey: [...sellerKeys.responses(assetId, versionId), page],
    queryFn: ({ signal }) => fetchSellerResponses(assetId, versionId, page, signal),
  })
  async function refresh() {
    await Promise.all([
      client.invalidateQueries({ queryKey: sellerKeys.responses(assetId, versionId) }),
      client.invalidateQueries({ queryKey: sellerKeys.versionReview(assetId, versionId) }),
      client.invalidateQueries({
        queryKey: sellerKeys.analysisFindingsForVersion(assetId, versionId),
      }),
    ])
  }
  const submitBody = useRef<Parameters<typeof submitSellerAssetVersion>[2] | null>(null)
  const submit = useMutation({
    mutationFn: () => {
      if (!query.data) throw new Error('Refresh evidence before submitting.')
      submitBody.current ??= {
        workspaceId: query.data.workspaceId,
        expectedWorkspaceRevision: query.data.workspaceRevision,
        expectedCaseRevision: query.data.caseRevision,
        operationId: crypto.randomUUID(),
      }
      return submitSellerAssetVersion(assetId, versionId, submitBody.current)
    },
    onSuccess: async (result) => {
      if (result.ok || !result.retryable) submitBody.current = null
      if (result.ok) toast.success('Submitted for human review.')
      else toast.error(result.message)
      await refresh()
    },
  })
  if (query.isPending) return <p role="status">Loading review responses...</p>
  if (query.isError)
    return (
      <p role="alert">
        Could not load responses.{' '}
        <Button type="button" onClick={() => void query.refetch()}>
          Refresh
        </Button>
      </p>
    )
  if (query.data.reportId !== reportId)
    return <p role="status">The selected report changed. Refresh version checks.</p>
  return (
    <section className="space-y-3 border-t pt-3" aria-label="Review responses">
      <p>
        {query.data.outstandingCount} of {query.data.requiredCount} required findings still need a
        response.
      </p>
      <p className="text-xs text-muted-foreground">
        Responses remain in history and support human review. They never resolve findings. Choose No
        evidence if you have nothing to add.
      </p>
      {findings
        .filter((finding) => finding.responseRequired)
        .map((finding) => (
          <EvidenceForm
            key={finding.id}
            assetId={assetId}
            versionId={versionId}
            current={query.data}
            finding={finding}
            refresh={refresh}
          />
        ))}
      <EvidenceForm
        assetId={assetId}
        versionId={versionId}
        current={query.data}
        refresh={refresh}
      />
      <details>
        <summary>Response history ({query.data.totalCount})</summary>
        <ul>
          {query.data.items.map((item) => (
            <li key={item.id} className="border-b py-2 text-xs">
              <p>
                {item.responseType.replaceAll('_', ' ')} - evidence revision {item.evidenceRevision}{' '}
                - {item.isCurrent ? 'Current answer' : 'Historical answer'}
              </p>
              <p className="whitespace-pre-wrap break-words">
                {item.text || 'No evidence supplied.'}
              </p>
            </li>
          ))}
        </ul>
        <Button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>
          Previous responses
        </Button>
        <span> Page {page} </span>
        <Button
          type="button"
          disabled={page >= query.data.totalPages}
          onClick={() => setPage(page + 1)}
        >
          Next responses
        </Button>
      </details>
      {canSubmit || submit.isError || (submit.data && !submit.data.ok && submit.data.retryable) ? (
        <Button
          type="button"
          className="rounded border px-3 py-2"
          disabled={submit.isPending}
          onClick={() => submit.mutate()}
        >
          {submit.isPending ? 'Submitting...' : 'Submit for review'}
        </Button>
      ) : null}
      {submit.data ? (
        <p role={submit.data.ok ? 'status' : 'alert'}>
          {submit.data.ok ? 'Submitted for human review.' : submit.data.message}
        </p>
      ) : null}
      {submit.isError ? (
        <p role="alert">
          {isTransportFailure(submit.error)
            ? 'Result unknown. Retry Submit for review to replay the same request.'
            : errorMessage(submit.error)}
        </p>
      ) : null}
      {submit.data && !submit.data.ok && submit.data.retryable ? (
        <p role="alert">Result unknown. Retry Submit for review to replay the same request.</p>
      ) : null}
    </section>
  )
}

function EvidenceForm({
  assetId,
  versionId,
  current,
  finding,
  refresh,
}: {
  assetId: string
  versionId: string
  current: SellerResponsesPage
  finding?: Finding
  refresh: () => Promise<void>
}) {
  const textId = useId()
  const typeId = useId()
  const form = useForm<SellerResponseFormValues>({
    resolver: zodResolver(sellerResponseFormSchema),
    defaultValues: { text: finding ? '' : current.evidenceText, responseType: 'NO_EVIDENCE' },
  })
  const baseline = useRef(current)
  const { isDirty, errors } = form.formState
  const pendingBody = useRef<SellerResponseRequest | SellerEvidenceEditRequest | null>(null)
  const mutation = useMutation({
    mutationFn: (values: SellerResponseFormValues) => {
      const expected = baseline.current
      if (!expected.reportId) throw new Error('No current report is available.')
      pendingBody.current ??= {
        workspaceId: expected.workspaceId,
        reportId: expected.reportId,
        expectedWorkspaceRevision: expected.workspaceRevision,
        expectedEvidenceRevision: expected.evidenceRevision,
        operationId: crypto.randomUUID(),
        text: values.text,
        ...(finding ? { findingId: finding.id, responseType: values.responseType } : {}),
      }
      return finding && 'findingId' in pendingBody.current
        ? saveSellerResponse(assetId, versionId, pendingBody.current)
        : saveSellerEvidence(assetId, versionId, pendingBody.current)
    },
    onSuccess: async (result, values) => {
      if (result.ok || !result.retryable) pendingBody.current = null
      if (result.ok) {
        form.reset(values)
        toast.success('Saved. History retained.')
      } else {
        toast.error(result.message)
        for (const name of ['text', 'responseType'] as const) {
          const message = result.fieldErrors?.[name]
          if (message) form.setError(name, { type: 'server', message })
        }
      }
      await refresh()
    },
  })
  const uncertain =
    isTransportFailure(mutation.error) ||
    Boolean(mutation.data && !mutation.data.ok && mutation.data.retryable)
  // Adopt server notes and revision only when clean. Dirty edits retain their original CAS basis.
  useEffect(() => {
    if (isDirty || mutation.isPending || uncertain) return
    baseline.current = current
    form.reset({ text: finding ? '' : current.evidenceText, responseType: 'NO_EVIDENCE' })
  }, [current, finding, form, isDirty, mutation.isPending, uncertain])
  const label = finding ? `Response for ${finding.ownPath}` : 'Additional review evidence'
  return (
    <form
      className="space-y-2 rounded border p-3 text-xs"
      onSubmit={form.handleSubmit((values) => mutation.mutate(values))}
    >
      <p className="font-medium">
        {label}
        {finding?.hasResponse ? ' - Answer recorded' : ''}
      </p>
      {finding ? (
        <Controller
          name="responseType"
          control={form.control}
          render={({ field }) => (
            <Field data-invalid={Boolean(errors.responseType)}>
              <FieldLabel htmlFor={typeId}>Response type</FieldLabel>
              <Select
                value={field.value}
                onValueChange={field.onChange}
                disabled={mutation.isPending || uncertain}
              >
                <SelectTrigger
                  id={typeId}
                  aria-label={`Response type for ${finding.ownPath}`}
                  aria-invalid={Boolean(errors.responseType)}
                  onBlur={field.onBlur}
                  ref={field.ref}
                >
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {sellerResponseTypeSchema.options.map((type) => (
                    <SelectItem key={type} value={type}>
                      {type.replaceAll('_', ' ')}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <FieldError errors={errors.responseType ? [errors.responseType] : undefined} />
            </Field>
          )}
        />
      ) : null}
      <Field data-invalid={Boolean(errors.text)}>
        <FieldLabel htmlFor={textId}>{finding ? 'Explanation' : 'Evidence notes'}</FieldLabel>
        <Textarea
          id={textId}
          aria-label={label}
          aria-invalid={Boolean(errors.text)}
          aria-describedby={errors.text ? `${textId}-error` : undefined}
          maxLength={SELLER_RESPONSE_TEXT_MAX}
          disabled={mutation.isPending || uncertain}
          {...form.register('text')}
        />
        <FieldError id={`${textId}-error`} errors={errors.text ? [errors.text] : undefined} />
      </Field>
      <p>Saving evidence supersedes an active review. Submit again explicitly after saving.</p>
      <Button
        variant="outline"
        size="sm"
        type={uncertain ? 'button' : 'submit'}
        onClick={uncertain ? () => mutation.mutate(form.getValues()) : undefined}
        disabled={mutation.isPending}
      >
        {mutation.isPending
          ? 'Saving...'
          : uncertain
            ? 'Retry same request'
            : finding
              ? 'Save response'
              : 'Save evidence notes'}
      </Button>
      {isDirty && !uncertain ? (
        <Button
          variant="ghost"
          size="sm"
          type="button"
          onClick={() => {
            baseline.current = current
            form.reset({ text: finding ? '' : current.evidenceText, responseType: 'NO_EVIDENCE' })
          }}
        >
          Use latest saved evidence
        </Button>
      ) : null}
      {mutation.data ? (
        <p role={mutation.data.ok ? 'status' : 'alert'}>
          {mutation.data.ok ? 'Saved. History retained.' : mutation.data.message}
        </p>
      ) : null}
      {mutation.isError && !uncertain ? <p role="alert">{errorMessage(mutation.error)}</p> : null}
      {uncertain ? (
        <p role="alert">Result unknown. Retry safely replays the same request.</p>
      ) : null}
    </form>
  )
}

function isTransportFailure(error: unknown): boolean {
  return error instanceof ApiRequestError && (error.status === 0 || error.status >= 500)
}
function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Could not save review evidence.'
}
