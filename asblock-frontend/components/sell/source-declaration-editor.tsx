'use client'

import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Controller, useFieldArray, useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { AlertCircle, Check, Loader2, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { z } from 'zod'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { fetchSellerDeclarationQuery, sellerKeys } from '@/lib/seller/seller-query'
import { saveSellerDeclaration } from '@/lib/seller/seller-draft-api'
import { createOperationTracker } from '@/lib/seller/draft-operations'
import {
  SELLER_COMPONENTS_MAX,
  SELLER_COMPONENT_ID_MAX,
  SELLER_COMPONENT_KNOWN_VERSION_MAX,
  SELLER_COMPONENT_LICENSE_MAX,
  SELLER_COMPONENT_NAME_MAX,
  SELLER_COMPONENT_NOTICE_ITEM_MAX,
  SELLER_COMPONENT_PATH_MAX,
  SELLER_COMPONENT_REF_ITEM_MAX,
  SELLER_COMPONENT_SOURCE_URL_MAX,
  SELLER_DECLARATION_MAX_BYTES,
  SELLER_EVIDENCE_REFS_MAX,
  SELLER_EXPLANATION_MAX,
  SELLER_NOTICE_LOCATIONS_MAX,
  SELLER_LICENSE_UNKNOWN,
  sourceDeclarationPayloadSchema,
  sourceDeclarationByteSize,
  type SourceDeclarationPayload,
} from '@/lib/seller/seller-draft-schemas'

const DISCLOSURE_POLICY_VERSION = 'disclosure-v1'

function csvToArray(value: string): string[] {
  return value
    .split(/[,;\n]+/)
    .map((item) => item.trim())
    .filter(Boolean)
}

const declarationComponentFormSchema = z.object({
  componentId: z.string().min(1).max(SELLER_COMPONENT_ID_MAX),
  name: z.string().min(1, 'Component name is required').max(SELLER_COMPONENT_NAME_MAX),
  origin: z.enum(['OWN_CONTRIBUTION', 'THIRD_PARTY']),
  packagePathOrRange: z.string().max(SELLER_COMPONENT_PATH_MAX),
  sourceUrl: z
    .string()
    .max(SELLER_COMPONENT_SOURCE_URL_MAX)
    .url('Enter a valid HTTPS source URL')
    .refine((url) => url.startsWith('https://'), 'Source URL must use HTTPS'),
  knownVersion: z.string().max(SELLER_COMPONENT_KNOWN_VERSION_MAX),
  license: z.string().min(1, 'License is required').max(SELLER_COMPONENT_LICENSE_MAX),
  noticeLocationsCsv: z
    .string()
    .max(SELLER_COMPONENT_NOTICE_ITEM_MAX * SELLER_NOTICE_LOCATIONS_MAX),
  modifications: z.string().max(SELLER_EXPLANATION_MAX),
  evidenceRefsCsv: z.string().max(SELLER_COMPONENT_REF_ITEM_MAX * SELLER_EVIDENCE_REFS_MAX),
})

const declarationFormSchema = z
  .object({
    ownContributionSummary: z.string().max(SELLER_EXPLANATION_MAX),
    ownChanges: z.string().max(SELLER_EXPLANATION_MAX),
    earlierWork: z.string().max(SELLER_EXPLANATION_MAX),
    redistributionAcknowledged: z.boolean(),
    disclosurePolicyVersion: z.string().min(1).max(64),
    components: z.array(declarationComponentFormSchema).max(SELLER_COMPONENTS_MAX),
  })
  .superRefine((values, ctx) => {
    values.components.forEach((component, index) => {
      const notices = csvToArray(component.noticeLocationsCsv)
      if (notices.length > SELLER_NOTICE_LOCATIONS_MAX) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['components', index, 'noticeLocationsCsv'],
          message: `At most ${SELLER_NOTICE_LOCATIONS_MAX} notice locations are allowed`,
        })
      } else if (notices.some((item) => item.length > SELLER_COMPONENT_NOTICE_ITEM_MAX)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['components', index, 'noticeLocationsCsv'],
          message: `Each notice location must be at most ${SELLER_COMPONENT_NOTICE_ITEM_MAX} characters`,
        })
      }

      const refs = csvToArray(component.evidenceRefsCsv)
      if (refs.length > SELLER_EVIDENCE_REFS_MAX) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['components', index, 'evidenceRefsCsv'],
          message: `At most ${SELLER_EVIDENCE_REFS_MAX} evidence references are allowed`,
        })
      } else if (refs.some((item) => item.length > SELLER_COMPONENT_REF_ITEM_MAX)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['components', index, 'evidenceRefsCsv'],
          message: `Each evidence reference must be at most ${SELLER_COMPONENT_REF_ITEM_MAX} characters`,
        })
      }
    })
  })

type DeclarationFormValues = z.infer<typeof declarationFormSchema>

function payloadFromForm(values: DeclarationFormValues): SourceDeclarationPayload {
  return {
    ownContributionSummary: values.ownContributionSummary.trim(),
    ownChanges: values.ownChanges.trim() || null,
    earlierWork: values.earlierWork.trim() || null,
    redistributionAcknowledged: values.redistributionAcknowledged,
    disclosurePolicyVersion: values.disclosurePolicyVersion,
    components: values.components.map((component) => ({
      componentId: component.componentId,
      name: component.name.trim(),
      packagePathOrRange: component.packagePathOrRange.trim() || null,
      sourceUrl: component.sourceUrl.trim(),
      knownVersion: component.knownVersion.trim() || null,
      license: component.license.trim(),
      noticeLocations: csvToArray(component.noticeLocationsCsv),
      modifications: component.modifications.trim() || null,
      permissionEvidenceReferences: csvToArray(component.evidenceRefsCsv),
      origin: component.origin,
    })),
  }
}

function emptyComponent(): DeclarationFormValues['components'][number] {
  return {
    componentId: crypto.randomUUID(),
    name: '',
    origin: 'THIRD_PARTY',
    packagePathOrRange: '',
    sourceUrl: '',
    knownVersion: '',
    license: SELLER_LICENSE_UNKNOWN,
    noticeLocationsCsv: '',
    modifications: '',
    evidenceRefsCsv: '',
  }
}

function formValuesFromPayload(payload: SourceDeclarationPayload | null): DeclarationFormValues {
  return {
    ownContributionSummary: payload?.ownContributionSummary ?? '',
    ownChanges: payload?.ownChanges ?? '',
    earlierWork: payload?.earlierWork ?? '',
    redistributionAcknowledged: payload?.redistributionAcknowledged ?? false,
    disclosurePolicyVersion: payload?.disclosurePolicyVersion ?? DISCLOSURE_POLICY_VERSION,
    components: (payload?.components ?? []).map((component) => ({
      componentId: component.componentId,
      name: component.name,
      origin: component.origin,
      packagePathOrRange: component.packagePathOrRange ?? '',
      sourceUrl: component.sourceUrl,
      knownVersion: component.knownVersion ?? '',
      license: component.license,
      noticeLocationsCsv: component.noticeLocations.join(', '),
      modifications: component.modifications ?? '',
      evidenceRefsCsv: component.permissionEvidenceReferences.join(', '),
    })),
  }
}

interface SourceDeclarationEditorProps {
  assetId: string
  /** Version workspace scope, or null for the pre-upload draft workspace. */
  versionId: string | null
  onDirtyChange?: (dirty: boolean) => void
  onPendingChange?: (pending: boolean) => void
}

export function SourceDeclarationEditor({
  assetId,
  versionId,
  onDirtyChange,
  onPendingChange,
}: SourceDeclarationEditorProps) {
  const queryClient = useQueryClient()
  const operationTracker = useRef(createOperationTracker())
  const [stale, setStale] = useState(false)
  const [serverDiverged, setServerDiverged] = useState(false)
  const [savedOnce, setSavedOnce] = useState(false)
  const [refreshPending, setRefreshPending] = useState(false)
  const [refreshError, setRefreshError] = useState<string | null>(null)
  const seededRef = useRef(false)
  const baselineRef = useRef<{ revision: number; payload: SourceDeclarationPayload | null } | null>(
    null,
  )
  const [baselineRevision, setBaselineRevision] = useState<number | null>(null)
  const [baselinePayload, setBaselinePayload] = useState<SourceDeclarationPayload | null>(null)

  const declarationQuery = useQuery({
    queryKey: sellerKeys.declaration(assetId, versionId),
    queryFn: () => fetchSellerDeclarationQuery({ assetId, versionId }),
  })

  const snapshot = declarationQuery.data
  const form = useForm<DeclarationFormValues>({
    resolver: zodResolver(declarationFormSchema),
    defaultValues: formValuesFromPayload(null),
  })
  const componentsArray = useFieldArray({ control: form.control, name: 'components' })
  useWatch({ control: form.control })
  const values = form.getValues()

  const setBaseline = (revision: number, payload: SourceDeclarationPayload | null) => {
    baselineRef.current = { revision, payload }
    setBaselineRevision(revision)
    setBaselinePayload(payload)
  }

  // Seed the form from the first fetched snapshot only; later snapshots never silently
  // rebase a dirty form onto a newer revision.
  useEffect(() => {
    if (!snapshot || seededRef.current) return
    seededRef.current = true
    form.reset(formValuesFromPayload(snapshot.declaration))
    setBaseline(snapshot.workspaceRevision, snapshot.declaration)
    setSavedOnce(true)
  }, [snapshot, form])

  // Adopt a newer server snapshot only when the editor is clean; otherwise flag the
  // divergence and let the seller reconcile explicitly.
  useEffect(() => {
    const baseline = baselineRef.current
    if (!snapshot || !baseline || snapshot.workspaceRevision === baseline.revision) return
    const dirty =
      JSON.stringify(payloadFromForm(form.getValues())) !== JSON.stringify(baseline.payload)
    if (dirty) {
      setServerDiverged(true)
      return
    }
    form.reset(formValuesFromPayload(snapshot.declaration))
    setBaseline(snapshot.workspaceRevision, snapshot.declaration)
    setServerDiverged(false)
  }, [snapshot, form])

  const savedPayloadJson = JSON.stringify(baselinePayload)
  const currentPayload = payloadFromForm(values)
  const currentPayloadJson = JSON.stringify(currentPayload)
  const unsaved = baselineRevision !== null && currentPayloadJson !== savedPayloadJson

  useEffect(() => {
    onDirtyChange?.(unsaved)
  }, [unsaved, onDirtyChange])
  useEffect(() => {
    onPendingChange?.(form.formState.isSubmitting)
  }, [form.formState.isSubmitting, onPendingChange])

  const byteSize = sourceDeclarationByteSize(currentPayload)
  const overSize = byteSize > SELLER_DECLARATION_MAX_BYTES

  // Authoritative status refresh. Runs after a confirmed save and on explicit retry;
  // it never performs a mutation. Always bypasses the cache — the cached snapshot
  // predates the save by definition.
  const refreshCompleteness = async (): Promise<boolean> => {
    try {
      // Direct server fetch: always current, and a failure never poisons the shared
      // declaration query cache (a failed fetchQuery would error the whole editor).
      const authoritative = await fetchSellerDeclarationQuery({ assetId, versionId })
      if (
        JSON.stringify(authoritative.declaration) === JSON.stringify(baselineRef.current?.payload)
      ) {
        setBaseline(authoritative.workspaceRevision, authoritative.declaration)
        queryClient.setQueryData(sellerKeys.declaration(assetId, versionId), authoritative)
      } else {
        setServerDiverged(true)
      }
      setRefreshError(null)
      return true
    } catch {
      setRefreshError(
        'Sources are saved, but the status refresh failed. Retry the refresh — there is no need to save again.',
      )
      return false
    }
  }

  const save = async (formValues: DeclarationFormValues) => {
    if (!baselineRef.current) return
    const declaration = payloadFromForm(formValues)
    const parsed = sourceDeclarationPayloadSchema.safeParse(declaration)
    if (!parsed.success) {
      toast.error('Fix the highlighted source declaration fields before saving.')
      return
    }
    if (sourceDeclarationByteSize(parsed.data) > SELLER_DECLARATION_MAX_BYTES) {
      toast.error('Source declaration must be at most 256 KiB.')
      return
    }

    setStale(false)
    // The envelope is pinned to the revision the edits were made against; a retry of a
    // lost committed response replays the exact same envelope.
    const payloadKey = `${versionId ?? 'pre-upload'}:${baselineRef.current.revision}:${currentPayloadJson}`
    let result
    try {
      result = await saveSellerDeclaration(assetId, versionId, {
        operationId: operationTracker.current.idFor(payloadKey),
        expectedWorkspaceRevision: baselineRef.current.revision,
        declaration: parsed.data,
      })
    } catch {
      // Unconfirmed mutation (network error): keep the exact envelope so a retry
      // replays it, keep the input, and explain the outcome.
      toast.error('Could not reach the server. Your edits are kept — try saving again.')
      return
    }

    if (!result.ok) {
      if (result.code === 'ERR_MODERATION_WORKSPACE_STALE') {
        setStale(true)
        toast.error(
          'The draft workspace changed. Load the latest version to continue; your edits are kept.',
        )
        return
      }
      toast.error(result.message)
      return
    }

    // The mutation is confirmed: commit the revision and the accepted payload now, so
    // even if the status refresh below fails the next edit builds on the saved state.
    operationTracker.current.reset()
    setStale(false)
    setSavedOnce(true)
    setBaseline(result.value.workspaceRevision, parsed.data)
    void queryClient.invalidateQueries({ queryKey: sellerKeys.draft(assetId) })
    toast.success(result.value.replayed ? 'Sources restored.' : 'Sources saved.')

    // Completeness refresh is separate from the save; its failure never undoes the save.
    setRefreshPending(true)
    try {
      await refreshCompleteness()
    } finally {
      setRefreshPending(false)
    }
  }

  const retryRefresh = async () => {
    setRefreshPending(true)
    try {
      await refreshCompleteness()
    } finally {
      setRefreshPending(false)
    }
  }

  const loadLatest = async () => {
    // Explicit reconcile must hit the server: a cached snapshot within staleTime is
    // exactly the stale revision that caused the conflict.
    try {
      // Direct server GET for the same reason as the status refresh above.
      const fresh = await fetchSellerDeclarationQuery({ assetId, versionId })
      // Only reset the tracker, local edits, and conflict state after the GET succeeds.
      operationTracker.current.reset()
      form.reset(formValuesFromPayload(fresh.declaration))
      setBaseline(fresh.workspaceRevision, fresh.declaration)
      setStale(false)
      setServerDiverged(false)
      setRefreshError(null)
      queryClient.setQueryData(sellerKeys.declaration(assetId, versionId), fresh)
    } catch {
      toast.error(
        'Could not load the latest declaration. Your edits and the conflict state are kept — try again.',
      )
    }
  }

  if (declarationQuery.isPending) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground py-4">
        <Loader2 className="size-4 animate-spin" aria-hidden />
        Loading source declaration…
      </div>
    )
  }

  if (declarationQuery.isError) {
    return (
      <div className="py-4 text-sm text-destructive" role="alert">
        {declarationQuery.error instanceof Error
          ? declarationQuery.error.message
          : 'Could not load the source declaration.'}
      </div>
    )
  }

  const complete = snapshot?.declarationComplete ?? false
  const blocked = stale || serverDiverged

  return (
    <form
      onSubmit={(event) => void form.handleSubmit(save)(event)}
      className="space-y-4 rounded-lg border border-border bg-card-elevated/30 p-4"
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold text-foreground">Source declaration</h3>
        {versionId !== null && (
          <span className="text-[11px] text-muted-foreground">
            Version sources — these do not change the declaration of the next upload.
          </span>
        )}
        <Badge variant="outline" className="text-[11px]">
          {refreshPending || refreshError
            ? 'Completeness not confirmed'
            : complete
              ? 'Complete'
              : 'Incomplete — saving is allowed'}
        </Badge>
        {unsaved ? (
          <Badge variant="outline" className="border-amber-500/40 text-[11px] text-amber-200">
            Unsaved changes
          </Badge>
        ) : savedOnce ? (
          <Badge variant="outline" className="border-emerald-500/40 text-[11px] text-emerald-200">
            <Check className="mr-1 size-3" aria-hidden />
            Saved
          </Badge>
        ) : null}
        {refreshPending && (
          <Badge variant="outline" className="text-[11px]">
            <Loader2 className="mr-1 size-3 animate-spin" aria-hidden />
            Refreshing status…
          </Badge>
        )}
        <span className="ml-auto text-[11px] text-muted-foreground">
          {byteSize.toLocaleString()} / {(SELLER_DECLARATION_MAX_BYTES / 1024).toFixed(0)} KiB
        </span>
      </div>

      {refreshError && !refreshPending && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-3">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-xs text-amber-800 dark:text-amber-200 flex flex-wrap items-center gap-2">
            {refreshError}
            <Button type="button" variant="outline" size="sm" onClick={() => void retryRefresh()}>
              <RefreshCw className="mr-1.5 size-3.5" aria-hidden />
              Retry refresh
            </Button>
          </AlertDescription>
        </Alert>
      )}

      {blocked && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-3">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-xs text-amber-800 dark:text-amber-200 flex flex-wrap items-center gap-2">
            The declaration changed on the server. Your edits below are kept. Load the latest
            version to continue saving — loading discards the local edits shown here.
            <Button type="button" variant="outline" size="sm" onClick={() => void loadLatest()}>
              <RefreshCw className="mr-1.5 size-3.5" aria-hidden />
              Load latest
            </Button>
          </AlertDescription>
        </Alert>
      )}

      <p className="text-xs text-muted-foreground">
        Source URLs are stored as data only — nothing is fetched, scanned, or executed. Use
        <span className="font-mono text-foreground/90"> {SELLER_LICENSE_UNKNOWN} </span>
        as the license when the origin is uncertain; it is never replaced with an ownership claim.
      </p>

      <div className="space-y-1.5">
        <Label htmlFor="declaration-own-summary" className="text-xs font-medium">
          Own contribution summary
        </Label>
        <Textarea
          id="declaration-own-summary"
          className="bg-input border-border h-24"
          placeholder="Describe your own contribution. You may leave this empty while the declaration is incomplete."
          {...form.register('ownContributionSummary')}
        />
        {form.formState.errors.ownContributionSummary && (
          <p className="text-xs text-destructive">
            {form.formState.errors.ownContributionSummary.message}
          </p>
        )}
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div className="space-y-1.5">
          <Label htmlFor="declaration-own-changes" className="text-xs font-medium">
            Own changes <span className="text-muted-foreground font-normal">(optional)</span>
          </Label>
          <Textarea
            id="declaration-own-changes"
            className="bg-input border-border h-20"
            {...form.register('ownChanges')}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="declaration-earlier-work" className="text-xs font-medium">
            Earlier work <span className="text-muted-foreground font-normal">(optional)</span>
          </Label>
          <Textarea
            id="declaration-earlier-work"
            className="bg-input border-border h-20"
            {...form.register('earlierWork')}
          />
        </div>
      </div>

      <label
        className="flex items-center gap-2 text-sm text-foreground"
        htmlFor="declaration-redistribution"
      >
        <Controller
          name="redistributionAcknowledged"
          control={form.control}
          render={({ field }) => (
            <Checkbox
              id="declaration-redistribution"
              ref={field.ref}
              name={field.name}
              checked={field.value}
              onCheckedChange={(checked) => field.onChange(checked === true)}
              onBlur={field.onBlur}
              disabled={field.disabled}
            />
          )}
        />
        I confirm the distribution rights and disclosure policy for this version.
      </label>

      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <h4 className="text-xs font-semibold text-foreground">
            Components ({componentsArray.fields.length}/{SELLER_COMPONENTS_MAX})
          </h4>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="border-border"
            disabled={componentsArray.fields.length >= SELLER_COMPONENTS_MAX}
            onClick={() => componentsArray.append(emptyComponent())}
          >
            <Plus className="mr-1.5 size-3.5" aria-hidden />
            Add component
          </Button>
        </div>

        {componentsArray.fields.map((field, index) => (
          <div key={field.id} className="rounded-md border border-border p-3 space-y-3">
            <input type="hidden" {...form.register(`components.${index}.componentId` as const)} />
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1.5">
                <Label htmlFor={`component-name-${index}`} className="text-xs font-medium">
                  Component name
                </Label>
                <Input
                  id={`component-name-${index}`}
                  className="bg-input border-border"
                  placeholder="e.g. left-pad"
                  {...form.register(`components.${index}.name` as const)}
                />
                {form.formState.errors.components?.[index]?.name && (
                  <p className="text-xs text-destructive">
                    {form.formState.errors.components[index]?.name?.message}
                  </p>
                )}
              </div>
              <div className="space-y-1.5">
                <Label htmlFor={`component-origin-${index}`} className="text-xs font-medium">
                  Origin
                </Label>
                <select
                  id={`component-origin-${index}`}
                  className="border-input bg-input h-9 w-full rounded-md border px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]"
                  {...form.register(`components.${index}.origin` as const)}
                >
                  <option value="THIRD_PARTY">Third-party</option>
                  <option value="OWN_CONTRIBUTION">Own contribution</option>
                </select>
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor={`component-url-${index}`} className="text-xs font-medium">
                Source URL (HTTPS)
              </Label>
              <Input
                id={`component-url-${index}`}
                className="bg-input border-border"
                placeholder="https://example.com/package"
                {...form.register(`components.${index}.sourceUrl` as const)}
              />
              {form.formState.errors.components?.[index]?.sourceUrl && (
                <p className="text-xs text-destructive">
                  {form.formState.errors.components[index]?.sourceUrl?.message}
                </p>
              )}
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1.5">
                <Label htmlFor={`component-path-${index}`} className="text-xs font-medium">
                  Package path or version range{' '}
                  <span className="text-muted-foreground font-normal">(optional)</span>
                </Label>
                <Input
                  id={`component-path-${index}`}
                  className="bg-input border-border"
                  {...form.register(`components.${index}.packagePathOrRange` as const)}
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor={`component-version-${index}`} className="text-xs font-medium">
                  Known version, commit, or tag{' '}
                  <span className="text-muted-foreground font-normal">(optional)</span>
                </Label>
                <Input
                  id={`component-version-${index}`}
                  className="bg-input border-border"
                  {...form.register(`components.${index}.knownVersion` as const)}
                />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor={`component-license-${index}`} className="text-xs font-medium">
                License
              </Label>
              <Input
                id={`component-license-${index}`}
                className="bg-input border-border"
                placeholder={SELLER_LICENSE_UNKNOWN}
                {...form.register(`components.${index}.license` as const)}
              />
              {form.formState.errors.components?.[index]?.license && (
                <p className="text-xs text-destructive">
                  {form.formState.errors.components[index]?.license?.message}
                </p>
              )}
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1.5">
                <Label htmlFor={`component-notices-${index}`} className="text-xs font-medium">
                  Notice locations{' '}
                  <span className="text-muted-foreground font-normal">(comma-separated)</span>
                </Label>
                <Input
                  id={`component-notices-${index}`}
                  className="bg-input border-border"
                  {...form.register(`components.${index}.noticeLocationsCsv` as const)}
                />
                {form.formState.errors.components?.[index]?.noticeLocationsCsv && (
                  <p className="text-xs text-destructive">
                    {form.formState.errors.components[index]?.noticeLocationsCsv?.message}
                  </p>
                )}
              </div>
              <div className="space-y-1.5">
                <Label htmlFor={`component-refs-${index}`} className="text-xs font-medium">
                  Permission evidence references{' '}
                  <span className="text-muted-foreground font-normal">(comma-separated)</span>
                </Label>
                <Input
                  id={`component-refs-${index}`}
                  className="bg-input border-border"
                  {...form.register(`components.${index}.evidenceRefsCsv` as const)}
                />
                {form.formState.errors.components?.[index]?.evidenceRefsCsv && (
                  <p className="text-xs text-destructive">
                    {form.formState.errors.components[index]?.evidenceRefsCsv?.message}
                  </p>
                )}
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor={`component-modifications-${index}`} className="text-xs font-medium">
                Modifications <span className="text-muted-foreground font-normal">(optional)</span>
              </Label>
              <Textarea
                id={`component-modifications-${index}`}
                className="bg-input border-border h-16"
                {...form.register(`components.${index}.modifications` as const)}
              />
            </div>

            <Button
              type="button"
              variant="outline"
              size="sm"
              className="border-destructive/40 text-destructive hover:bg-destructive/10"
              onClick={() => componentsArray.remove(index)}
            >
              <Trash2 className="mr-1.5 size-3.5" aria-hidden />
              Remove component
            </Button>
          </div>
        ))}
      </div>

      {overSize && (
        <p className="text-xs text-destructive" role="alert">
          Source declaration is over the 256 KiB limit. Remove components or shorten text.
        </p>
      )}

      <Button
        type="submit"
        disabled={form.formState.isSubmitting || overSize || blocked || !snapshot}
        className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
      >
        {form.formState.isSubmitting ? (
          <>
            <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
            Saving sources…
          </>
        ) : (
          'Save sources'
        )}
      </Button>
    </form>
  )
}
