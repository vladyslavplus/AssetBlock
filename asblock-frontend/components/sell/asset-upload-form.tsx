'use client'

import { useState } from 'react'
import { useRouter } from 'next/navigation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { AlertCircle, Loader2 } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import Link from 'next/link'
import { routes } from '@/lib/routes'
import { applyApiFieldErrorsToForm } from '@/lib/http/api-errors'
import {
  assetDraftCreateFormSchema,
  type AssetDraftCreateFormValues,
} from '@/lib/seller/seller-schemas'
import { createSellerDraft } from '@/lib/seller/seller-draft-api'
import { createOperationTracker } from '@/lib/seller/draft-operations'
import { catalogKeys, fetchCatalogFacets } from '@/lib/catalog/catalog-query'
import { sellerKeys } from '@/lib/seller/seller-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { SellerPriceStepInput } from '@/components/sell/seller-price-step-input'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'

function draftCreatePayloadKey(values: AssetDraftCreateFormValues): string {
  return JSON.stringify([
    values.title.trim(),
    values.description?.trim() ?? '',
    values.price,
    values.categoryId,
  ])
}

export function AssetUploadForm() {
  const router = useRouter()
  const queryClient = useQueryClient()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)
  const [operationTracker] = useState(() => createOperationTracker())
  const [failure, setFailure] = useState<string | null>(null)

  const facetsQuery = useQuery({
    queryKey: catalogKeys.facets(),
    queryFn: () => fetchCatalogFacets(),
    staleTime: 5 * 60 * 1000,
    enabled: authed,
  })
  const categories = facetsQuery.data?.categories ?? []
  const categoriesLoading = authed && facetsQuery.isPending
  const categoriesError = facetsQuery.isError ? 'Could not load categories.' : null

  const {
    register,
    control,
    setError,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<AssetDraftCreateFormValues>({
    resolver: zodResolver(assetDraftCreateFormSchema),
    defaultValues: {
      title: '',
      description: '',
      price: undefined,
      categoryId: '',
    },
  })

  const onSubmit = handleSubmit(async (values) => {
    setFailure(null)
    let result
    try {
      result = await createSellerDraft({
        operationId: operationTracker.idFor(draftCreatePayloadKey(values)),
        title: values.title.trim(),
        description: values.description?.trim() || null,
        price: values.price,
        categoryId: values.categoryId,
        downloadLimitPerHour: null,
      })
    } catch {
      setFailure('Could not confirm draft creation. Your input is kept — retry to confirm it.')
      return
    }
    if (!result.ok) {
      if (result.fieldErrors) {
        applyApiFieldErrorsToForm(setError, result.fieldErrors)
      }
      setFailure(result.message)
      toast.error(result.message)
      return
    }

    toast.success(
      result.value.replayed ? 'Draft restored.' : 'Draft saved. Upload files when you are ready.',
    )
    operationTracker.reset()
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.all })
    router.push(routes.sellerAssetEdit(result.value.assetId))
  })

  if (pending) {
    return <SessionBlockSkeleton lines={3} />
  }

  if (!authed) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
        <p className="text-sm text-muted-foreground">Sign in to create a listing.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href={routes.login(routes.sell())}>Sign in</Link>
        </Button>
      </div>
    )
  }

  if (!verified) {
    return <EmailVerificationNotice />
  }

  return (
    <form onSubmit={onSubmit} className="space-y-5 max-w-lg">
      {categoriesError && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-2">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-amber-800 dark:text-amber-200 text-xs">
            {categoriesError}
          </AlertDescription>
        </Alert>
      )}

      {failure && (
        <Alert className="border-destructive/40 bg-destructive/10 py-2">
          <AlertCircle className="h-4 w-4 text-destructive" />
          <AlertDescription className="text-xs text-destructive" role="alert">
            {failure}
          </AlertDescription>
        </Alert>
      )}

      <div className="space-y-1.5">
        <Label htmlFor="upload-title" className="text-xs font-medium">
          Title
        </Label>
        <Input
          id="upload-title"
          className="bg-input border-border"
          placeholder="e.g. SaaS dashboard boilerplate"
          {...register('title')}
        />
        {errors.title && <p className="text-xs text-destructive">{errors.title.message}</p>}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="upload-description" className="text-xs font-medium">
          Description <span className="text-muted-foreground font-normal">(optional)</span>
        </Label>
        <Textarea
          id="upload-description"
          className="bg-input border-border h-44 sm:h-40 md:h-36"
          placeholder="What buyers get, stack, license notes…"
          {...register('description')}
        />
        {errors.description && (
          <p className="text-xs text-destructive">{errors.description.message}</p>
        )}
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div className="space-y-1.5">
          <Label htmlFor="upload-price" className="text-xs font-medium">
            Price (USD)
          </Label>
          <Controller
            name="price"
            control={control}
            render={({ field }) => (
              <SellerPriceStepInput
                id="upload-price"
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
          <Label htmlFor="upload-category" className="text-xs font-medium">
            Category
          </Label>
          {categoriesLoading ? (
            <Skeleton
              className="h-9 w-full rounded-md bg-muted-foreground/20 animate-pulse"
              aria-busy="true"
              aria-label="Loading categories"
            />
          ) : (
            <select
              id="upload-category"
              className="border-input bg-input h-9 w-full rounded-md border px-3 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]"
              defaultValue=""
              {...register('categoryId')}
            >
              <option value="" disabled>
                Select category
              </option>
              {categories.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          )}
          {errors.categoryId && (
            <p className="text-xs text-destructive">{errors.categoryId.message}</p>
          )}
        </div>
      </div>

      <p className="text-[11px] text-muted-foreground">
        This creates a private draft — no file yet. You will pick the license and upload the package
        from the draft page.
      </p>

      <Button
        type="submit"
        disabled={
          isSubmitting || categoriesLoading || Boolean(categoriesError && categories.length === 0)
        }
        className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
      >
        {isSubmitting ? (
          <>
            <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
            Saving draft…
          </>
        ) : (
          'Create draft'
        )}
      </Button>
    </form>
  )
}
