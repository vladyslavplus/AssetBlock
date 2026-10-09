'use client'

import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useRouter } from 'next/navigation'
import Link from 'next/link'
import { Controller, useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { AlertCircle, Loader2, RefreshCw } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import type { SellerAssetDetail } from '@/lib/seller/seller-asset-schemas'
import { applyApiFieldErrorsToForm } from '@/lib/http/api-errors'
import { assetEditFormSchema, type AssetEditFormValues } from '@/lib/seller/seller-schemas'
import { fetchSellerDraftQuery, sellerKeys } from '@/lib/seller/seller-query'
import { saveSellerDraft } from '@/lib/seller/seller-draft-api'
import { patchSellerAssetPrice } from '@/lib/seller/seller-api'
import { createOperationTracker } from '@/lib/seller/draft-operations'
import { assetKeys } from '@/lib/catalog/asset-detail-query'
import { catalogKeys, fetchCatalogFacets } from '@/lib/catalog/catalog-query'
import {
  getSellerProcessingBadgeClass,
  getSellerProcessingStatusDescription,
  getSellerProcessingStatusLabel,
} from '@/lib/seller/seller-processing-status'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { SellerPriceStepInput } from '@/components/sell/seller-price-step-input'
import { SellerAssetVersionsSection } from '@/components/sell/seller-asset-versions-section'
import { SourceDeclarationEditor } from '@/components/sell/source-declaration-editor'
import { ListingCopilotPanel } from '@/components/sell/listing-copilot-panel'

interface AssetEditFormProps {
  asset: SellerAssetDetail
}

function csvFromTags(tags: string[] | null | undefined): string {
  return (tags ?? []).join(', ')
}

function tagsFromCsv(csv: string | undefined): string[] {
  return (csv ?? '')
    .split(/[,;\n]+/)
    .map((tag) => tag.trim())
    .filter(Boolean)
}

export function AssetEditForm({ asset }: AssetEditFormProps) {
  const router = useRouter()
  const queryClient = useQueryClient()
  const assetId = asset.id
  const operationTracker = useRef(createOperationTracker())
  const [stale, setStale] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)

  const draftQuery = useQuery({
    queryKey: sellerKeys.draft(assetId),
    queryFn: () => fetchSellerDraftQuery({ assetId }),
  })
  const draft = draftQuery.data

  const facetsQuery = useQuery({
    queryKey: catalogKeys.facets(),
    queryFn: () => fetchCatalogFacets(),
    staleTime: 5 * 60 * 1000,
  })
  const categories = facetsQuery.data?.categories ?? []
  const categoriesError = facetsQuery.isError ? 'Could not load categories.' : null

  const form = useForm<AssetEditFormValues>({
    resolver: zodResolver(assetEditFormSchema),
    defaultValues: {
      title: asset.title,
      description: asset.description ?? '',
      price: Number(asset.price),
      categoryId: asset.categoryId,
      tags: csvFromTags(asset.tags),
    },
  })

  const draftSeedApplied = useRef(false)
  const [materialDiverged, setMaterialDiverged] = useState(false)
  const [declarationDirty, setDeclarationDirty] = useState(false)
  const [declarationPending, setDeclarationPending] = useState(false)
  const materialBaselineRef = useRef<{ revision: number; key: string } | null>(null)
  const [materialBaseline, setBaselineState] = useState<{ revision: number; key: string } | null>(
    null,
  )
  const setMaterialBaseline = (revision: number, key: string) => {
    materialBaselineRef.current = { revision, key }
    setBaselineState({ revision, key })
  }

  const materialKeyOfDraft = (material: {
    title: string
    description: string | null
    categoryId: string
    tags: string[] | null
  }) =>
    JSON.stringify([
      material.title,
      material.description ?? '',
      material.categoryId,
      material.tags ?? [],
    ])

  useEffect(() => {
    if (draft && !draftSeedApplied.current) {
      form.reset({
        title: draft.material.title,
        description: draft.material.description ?? '',
        price: Number(asset.price),
        categoryId: draft.material.categoryId,
        tags: csvFromTags(draft.material.tags),
      })
      setMaterialBaseline(draft.workspaceRevision, materialKeyOfDraft(draft.material))
      draftSeedApplied.current = true
    }
  }, [asset.price, draft, form])

  // Adopt a newer draft snapshot only when the editor is clean; a dirty editor keeps its
  // baseline revision so the next save cannot send old payload with a newer revision.
  useWatch({ control: form.control })
  const values = form.getValues()
  const materialKey = JSON.stringify([
    values.title?.trim() ?? '',
    values.description?.trim() ?? '',
    values.categoryId,
    tagsFromCsv(values.tags),
  ])
  useEffect(() => {
    const baseline = materialBaselineRef.current
    if (!draft || !baseline || draft.workspaceRevision === baseline.revision) return
    // Read live form values (getValues) so the comparison never uses a stale render
    // closure; the adopted payload must reflect exactly what the seller sees.
    const current = form.getValues()
    const key = JSON.stringify([
      current.title.trim(),
      current.description?.trim() ?? '',
      current.categoryId,
      tagsFromCsv(current.tags),
    ])
    if (key === baseline.key) {
      // Adopt atomically from one snapshot: shown fields AND baseline revision, so a
      // later save cannot send the old payload under the new revision. The price is a
      // separate mutation contract and is never overwritten here.
      form.reset({
        title: draft.material.title,
        description: draft.material.description ?? '',
        price: Number(form.getValues('price')),
        categoryId: draft.material.categoryId,
        tags: csvFromTags(draft.material.tags),
      })
      setMaterialBaseline(draft.workspaceRevision, materialKeyOfDraft(draft.material))
      setMaterialDiverged(false)
    } else {
      setMaterialDiverged(true)
    }
  }, [draft, form])

  const materialDirty = materialBaseline !== null && materialKey !== materialBaseline.key
  const materialBlocked = stale || materialDiverged
  const priceDirty = draft !== undefined && Number(values.price) !== Number(asset.price)

  const uploadBlockers = [
    ...(materialDirty || form.formState.isSubmitting ? ['Draft metadata'] : []),
    ...(declarationDirty || declarationPending ? ['Source declaration'] : []),
  ]

  const loadLatestDraft = async () => {
    // Explicit reconcile must hit the server: a cached snapshot within staleTime is
    // exactly the stale revision that caused the conflict.
    try {
      // Direct server GET: a reconcile must see the latest saved draft, and a failed
      // fetch must not error the shared draft query (which would tear down the form).
      const fresh = await fetchSellerDraftQuery({ assetId })
      // Only clear local edits and conflict state after the fresh snapshot is in hand.
      operationTracker.current.reset()
      form.reset({
        title: fresh.material.title,
        description: fresh.material.description ?? '',
        price: Number(form.getValues('price')),
        categoryId: fresh.material.categoryId,
        tags: csvFromTags(fresh.material.tags),
      })
      setMaterialBaseline(fresh.workspaceRevision, materialKeyOfDraft(fresh.material))
      setStale(false)
      setMaterialDiverged(false)
      queryClient.setQueryData(sellerKeys.draft(assetId), fresh)
    } catch {
      toast.error('Could not load the latest draft. Your edits are kept — try again.')
    }
  }

  const onSubmit = async (formValues: AssetEditFormValues) => {
    setStale(false)
    setFailure(null)

    let materialOutcome: 'ok' | 'failed' | 'skipped' = 'skipped'
    let priceOutcome: 'ok' | 'failed' | 'skipped' = 'skipped'
    let firstFailure: string | null = null

    if (materialDirty && materialBaselineRef.current) {
      const material = {
        title: formValues.title.trim(),
        description: formValues.description?.trim() || null,
        categoryId: formValues.categoryId,
        tags: tagsFromCsv(formValues.tags),
      }
      let result
      try {
        result = await saveSellerDraft(assetId, {
          operationId: operationTracker.current.idFor(
            `${materialBaselineRef.current.revision}:${materialKey}`,
          ),
          expectedWorkspaceRevision: materialBaselineRef.current.revision,
          material,
        })
      } catch {
        setFailure('Could not confirm the draft save. Your edits are kept — retry to confirm it.')
        return
      }
      if (!result.ok) {
        materialOutcome = 'failed'
        firstFailure = firstFailure ?? result.message
        if (result.code === 'ERR_MODERATION_WORKSPACE_STALE') {
          setStale(true)
        } else {
          setFailure(result.message)
        }
      } else {
        materialOutcome = 'ok'
        operationTracker.current.reset()
        setMaterialBaseline(result.value.workspaceRevision, materialKey)
        setMaterialDiverged(false)
        queryClient.setQueryData(sellerKeys.draft(assetId), {
          ...draft,
          workspaceRevision: result.value.workspaceRevision,
          material,
        })
        invalidateQueriesInBackground(queryClient, {
          queryKey: [...sellerKeys.all, 'declaration', assetId],
        })
        invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.listings() })
        invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.detail(assetId) })
      }
    }

    if (priceDirty) {
      let priceResult
      try {
        priceResult = await patchSellerAssetPrice(assetId, formValues.price)
      } catch {
        setFailure(
          materialOutcome === 'ok'
            ? 'Draft saved, but the price update could not be confirmed. Retry the price update.'
            : 'The price update could not be confirmed. Your input is kept — retry the update.',
        )
        return
      }
      if (!priceResult.ok) {
        priceOutcome = 'failed'
        firstFailure = firstFailure ?? priceResult.message
        if (priceResult.fieldErrors) {
          applyApiFieldErrorsToForm(form.setError, priceResult.fieldErrors)
        }
      } else {
        priceOutcome = 'ok'
        invalidateQueriesInBackground(queryClient, { queryKey: catalogKeys.all })
        invalidateQueriesInBackground(queryClient, { queryKey: assetKeys.detail(assetId) })
        invalidateQueriesInBackground(queryClient, { queryKey: assetKeys.similarAll })
        invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.listings() })
        invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.detail(assetId) })
      }
    }

    if (materialOutcome === 'failed' || priceOutcome === 'failed') {
      if (!stale) {
        toast.error(firstFailure ?? 'Not everything was saved.')
      }
      if (materialOutcome === 'ok' && priceOutcome === 'failed') {
        toast.info('Draft saved, but the price was not updated. Try updating the price again.')
      }
      if (materialOutcome === 'failed' && priceOutcome === 'ok') {
        toast.info('Price updated, but the draft material was not saved. Try saving again.')
      }
      return
    }

    if (materialOutcome === 'ok' || priceOutcome === 'ok') {
      toast.success(
        materialOutcome === 'ok' && priceOutcome === 'ok'
          ? 'Draft and price saved.'
          : materialOutcome === 'ok'
            ? 'Draft saved. Upload files when you are ready.'
            : 'Price updated.',
      )
    } else {
      toast.info('Nothing to save yet.')
    }
    router.refresh()
  }

  return (
    <div className="space-y-6 max-w-lg">
      <Alert className="border-border bg-card-elevated/40 py-3">
        <AlertDescription className="flex flex-col gap-2">
          <Badge
            variant="outline"
            className={getSellerProcessingBadgeClass(asset.latestProcessingStatus)}
          >
            {asset.latestVersionId == null
              ? 'Draft'
              : getSellerProcessingStatusLabel(asset.latestProcessingStatus)}
          </Badge>
          <p className="text-xs text-muted-foreground">
            {asset.latestVersionId == null
              ? 'No package uploaded yet. Save metadata and sources first; the upload stays optional.'
              : getSellerProcessingStatusDescription(asset.latestProcessingStatus)}
          </p>
          {asset.latestProcessingErrorSummary &&
          (asset.latestProcessingStatus === 'REJECTED' ||
            asset.latestProcessingStatus === 'PROCESSING_FAILED') ? (
            <p className="text-xs text-destructive">{asset.latestProcessingErrorSummary}</p>
          ) : null}
        </AlertDescription>
      </Alert>

      {categoriesError && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-2">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-amber-800 dark:text-amber-200 text-xs">
            {categoriesError}
          </AlertDescription>
        </Alert>
      )}

      {(stale || materialDiverged) && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-3">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-xs text-amber-800 dark:text-amber-200 flex flex-wrap items-center gap-2">
            The draft workspace changed. Your edits are kept. Load the latest draft to continue —
            loading replaces the local edits shown here with the saved draft.
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => void loadLatestDraft()}
            >
              <RefreshCw className="mr-1.5 size-3.5" aria-hidden />
              Load latest
            </Button>
          </AlertDescription>
        </Alert>
      )}

      {failure && !stale && (
        <Alert className="border-destructive/40 bg-destructive/10 py-2">
          <AlertCircle className="h-4 w-4 text-destructive" />
          <AlertDescription className="text-xs text-destructive" role="alert">
            {failure}
          </AlertDescription>
        </Alert>
      )}

      <form onSubmit={(event) => void form.handleSubmit(onSubmit)(event)} className="space-y-5">
        <div className="space-y-1.5">
          <Label htmlFor="edit-title" className="text-xs font-medium">
            Title
          </Label>
          <Input id="edit-title" className="bg-input border-border" {...form.register('title')} />
          {form.formState.errors.title && (
            <p className="text-xs text-destructive">{form.formState.errors.title.message}</p>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="edit-description" className="text-xs font-medium">
            Description <span className="text-muted-foreground font-normal">(optional)</span>
          </Label>
          <Textarea
            id="edit-description"
            className="bg-input border-border h-44 sm:h-40 md:h-36"
            {...form.register('description')}
          />
          {form.formState.errors.description && (
            <p className="text-xs text-destructive">{form.formState.errors.description.message}</p>
          )}
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div className="space-y-1.5">
            <Label htmlFor="edit-price" className="text-xs font-medium">
              Price (USD)
            </Label>
            <Controller
              name="price"
              control={form.control}
              render={({ field }) => (
                <SellerPriceStepInput
                  id="edit-price"
                  aria-label="Price in USD"
                  value={field.value}
                  onChange={field.onChange}
                  onBlur={field.onBlur}
                />
              )}
            />
            {form.formState.errors.price && (
              <p className="text-xs text-destructive">{form.formState.errors.price.message}</p>
            )}
            <p className="text-[11px] text-muted-foreground">
              Price is saved as its own operation and does not create a draft revision.
            </p>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="edit-category" className="text-xs font-medium">
              Category
            </Label>
            <select
              id="edit-category"
              className="border-input bg-input h-9 w-full rounded-md border px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]"
              {...form.register('categoryId')}
            >
              {categories.length === 0 ? (
                <option value={form.getValues('categoryId')}>
                  {asset.categoryName ?? 'Current category'}
                </option>
              ) : (
                categories.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))
              )}
            </select>
            {form.formState.errors.categoryId && (
              <p className="text-xs text-destructive">{form.formState.errors.categoryId.message}</p>
            )}
          </div>
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="edit-tags" className="text-xs font-medium">
            Tags <span className="text-muted-foreground font-normal">(optional)</span>
          </Label>
          <Input
            id="edit-tags"
            className="bg-input border-border"
            placeholder="react, typescript, dashboard"
            {...form.register('tags')}
          />
          <p className="text-[11px] text-muted-foreground">
            Comma-separated. Tags are part of the draft material and are saved with it.
          </p>
        </div>

        {asset.currentReadyVersionId ? (
          <ListingCopilotPanel
            assetId={assetId}
            assetVersionId={asset.currentReadyVersionId}
            categories={categories}
            catalogTags={facetsQuery.data?.tags ?? []}
            setValue={form.setValue}
            getValues={form.getValues}
            dirtyFields={form.formState.dirtyFields}
          />
        ) : null}

        <div className="flex flex-col sm:flex-row gap-3 pt-2">
          <Button
            type="submit"
            disabled={
              form.formState.isSubmitting ||
              materialBlocked ||
              Boolean(categoriesError && categories.length === 0)
            }
            className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
          >
            {form.formState.isSubmitting ? (
              <>
                <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
                Saving…
              </>
            ) : (
              'Save draft'
            )}
          </Button>
          <Button
            type="button"
            variant="outline"
            className="border-border w-full sm:w-auto"
            asChild
          >
            <Link href="/sell?tab=listings">Back to listings</Link>
          </Button>
        </div>
      </form>

      <SourceDeclarationEditor
        assetId={assetId}
        versionId={null}
        onDirtyChange={setDeclarationDirty}
        onPendingChange={setDeclarationPending}
      />

      <SellerAssetVersionsSection assetId={assetId} uploadBlockers={uploadBlockers} />
    </div>
  )
}
