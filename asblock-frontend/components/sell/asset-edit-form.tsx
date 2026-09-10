'use client'

import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useRouter } from 'next/navigation'
import Link from 'next/link'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Loader2, AlertCircle } from 'lucide-react'
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
import { fetchTagNameToIdMap, patchSellerAsset, syncSellerAssetTags } from '@/lib/seller/seller-api'
import { assetKeys } from '@/lib/catalog/asset-detail-query'
import { catalogKeys, fetchCatalogFacets } from '@/lib/catalog/catalog-query'
import { sellerKeys } from '@/lib/seller/seller-query'
import { routes } from '@/lib/routes'
import {
  getSellerProcessingBadgeClass,
  getSellerProcessingStatusDescription,
  getSellerProcessingStatusLabel,
} from '@/lib/seller/seller-processing-status'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { SellerPriceStepInput } from '@/components/sell/seller-price-step-input'
import { SellerAssetVersionsSection } from '@/components/sell/seller-asset-versions-section'
import { ListingCopilotPanel } from '@/components/sell/listing-copilot-panel'

interface AssetEditFormProps {
  asset: SellerAssetDetail
}

export function AssetEditForm({ asset }: AssetEditFormProps) {
  const router = useRouter()
  const queryClient = useQueryClient()
  const assetId = asset.id
  const publiclyReady = asset.currentReadyVersionId != null

  const facetsQuery = useQuery({
    queryKey: catalogKeys.facets(),
    queryFn: () => fetchCatalogFacets(),
    staleTime: 5 * 60 * 1000,
  })

  const categories = facetsQuery.data?.categories ?? []
  const categoriesError = facetsQuery.isError ? 'Could not load categories.' : null

  const initialTagsCsv = (asset.tags ?? []).join(', ')

  const initialTagKeys = (asset.tags ?? []).map((t) => t.trim().toLowerCase()).filter(Boolean)

  const {
    register,
    control,
    setError,
    setValue,
    getValues,
    handleSubmit,
    formState: { errors, isSubmitting, dirtyFields },
  } = useForm<AssetEditFormValues>({
    resolver: zodResolver(assetEditFormSchema),
    defaultValues: {
      title: asset.title,
      description: asset.description ?? '',
      price: Number(asset.price),
      categoryId: asset.categoryId,
      tags: initialTagsCsv,
    },
  })

  const onSubmit = handleSubmit(async (values) => {
    const desc = values.description?.trim() ?? ''
    const patch = await patchSellerAsset(assetId, {
      title: values.title.trim(),
      description: desc.length > 0 ? desc : '',
      price: values.price,
      categoryId: values.categoryId,
    })
    if (!patch.ok) {
      if (patch.fieldErrors) {
        applyApiFieldErrorsToForm(setError, patch.fieldErrors)
      }
      toast.error(patch.message)
      return
    }

    let lookup: Map<string, string>
    try {
      lookup = await fetchTagNameToIdMap()
    } catch {
      toast.error('Could not load the tags catalog. Tags were not updated.')
      return
    }

    const tagSync = await syncSellerAssetTags(assetId, initialTagKeys, values.tags, lookup)
    if (!tagSync.ok) {
      toast.error(tagSync.message)
      return
    }

    toast.success('Asset updated.')
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.all })
    invalidateQueriesInBackground(queryClient, { queryKey: catalogKeys.all })
    invalidateQueriesInBackground(queryClient, { queryKey: assetKeys.detail(assetId) })
    if (publiclyReady) {
      router.push(routes.assetDetail(assetId))
    }
    router.refresh()
  })

  return (
    <div className="space-y-6 max-w-lg">
      <Alert className="border-border bg-card-elevated/40 py-3">
        <AlertDescription className="flex flex-col gap-2">
          <Badge
            variant="outline"
            className={getSellerProcessingBadgeClass(asset.latestProcessingStatus)}
          >
            {getSellerProcessingStatusLabel(asset.latestProcessingStatus)}
          </Badge>
          <p className="text-xs text-muted-foreground">
            {getSellerProcessingStatusDescription(asset.latestProcessingStatus)}
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

      <form onSubmit={onSubmit} className="space-y-5">
        <div className="space-y-1.5">
          <Label htmlFor="edit-title" className="text-xs font-medium">
            Title
          </Label>
          <Input id="edit-title" className="bg-input border-border" {...register('title')} />
          {errors.title && <p className="text-xs text-destructive">{errors.title.message}</p>}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="edit-description" className="text-xs font-medium">
            Description <span className="text-muted-foreground font-normal">(optional)</span>
          </Label>
          <Textarea
            id="edit-description"
            className="bg-input border-border h-44 sm:h-40 md:h-36"
            {...register('description')}
          />
          {errors.description && (
            <p className="text-xs text-destructive">{errors.description.message}</p>
          )}
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div className="space-y-1.5">
            <Label htmlFor="edit-price" className="text-xs font-medium">
              Price (USD)
            </Label>
            <Controller
              name="price"
              control={control}
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
            {errors.price && <p className="text-xs text-destructive">{errors.price.message}</p>}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="edit-category" className="text-xs font-medium">
              Category
            </Label>
            <select
              id="edit-category"
              className="border-input bg-input h-9 w-full rounded-md border px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]"
              {...register('categoryId')}
            >
              {categories.length === 0 ? (
                <option value={asset.categoryId}>{asset.categoryName ?? 'Current category'}</option>
              ) : (
                categories.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))
              )}
            </select>
            {errors.categoryId && (
              <p className="text-xs text-destructive">{errors.categoryId.message}</p>
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
            {...register('tags')}
          />
          <p className="text-[11px] text-muted-foreground">
            Comma-separated. Only tags that exist in the catalog can be added (same as when
            uploading).
          </p>
        </div>

        {asset.currentReadyVersionId ? (
          <ListingCopilotPanel
            assetId={assetId}
            assetVersionId={asset.currentReadyVersionId}
            categories={categories}
            catalogTags={facetsQuery.data?.tags ?? []}
            setValue={setValue}
            getValues={getValues}
            dirtyFields={dirtyFields}
          />
        ) : null}

        <div className="flex flex-col sm:flex-row gap-3 pt-2">
          <Button
            type="submit"
            disabled={isSubmitting || Boolean(categoriesError && categories.length === 0)}
            className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
          >
            {isSubmitting ? (
              <>
                <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
                Saving…
              </>
            ) : (
              'Save changes'
            )}
          </Button>
          <Button
            type="button"
            variant="outline"
            className="border-border w-full sm:w-auto"
            asChild
          >
            <Link href={publiclyReady ? routes.assetDetail(assetId) : routes.sell()}>Cancel</Link>
          </Button>
        </div>
      </form>

      <SellerAssetVersionsSection assetId={assetId} />
    </div>
  )
}
