'use client'

import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useRouter } from 'next/navigation'
import { Controller, useForm, useWatch } from 'react-hook-form'
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
import Link from 'next/link'
import { applyApiFieldErrorsToForm } from '@/lib/http/api-errors'
import {
  ASSET_UPLOAD_ALLOWED_EXTENSIONS,
  assetUploadFormSchema,
  type AssetUploadFormValues,
} from '@/lib/seller/seller-schemas'
import { uploadSellerAsset } from '@/lib/seller/seller-api'
import { catalogKeys, fetchCatalogFacets } from '@/lib/catalog/catalog-query'
import { sellerKeys } from '@/lib/seller/seller-query'
import { SellerPriceStepInput } from '@/components/sell/seller-price-step-input'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'

export function AssetUploadForm() {
  const router = useRouter()
  const queryClient = useQueryClient()
  const { status } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'

  const facetsQuery = useQuery({
    queryKey: catalogKeys.facets(),
    queryFn: fetchCatalogFacets,
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
    trigger,
    handleSubmit,
    formState: { errors, isSubmitting },
    reset,
  } = useForm<AssetUploadFormValues>({
    resolver: zodResolver(assetUploadFormSchema),
    defaultValues: {
      title: '',
      description: '',
      price: undefined,
      categoryId: '',
      tags: '',
    },
  })

  const selectedFile = useWatch({ control, name: 'file' })
  const fileDisplayName =
    selectedFile instanceof File && selectedFile.name.length > 0
      ? selectedFile.name
      : 'No file chosen'

  const onSubmit = handleSubmit(async (values) => {
    const fd = new FormData()
    fd.set('title', values.title.trim())
    const desc = values.description?.trim()
    if (desc) fd.set('description', desc)
    fd.set('price', String(values.price))
    fd.set('categoryId', values.categoryId)
    fd.set('file', values.file)

    const tagParts = (values.tags ?? '')
      .split(/[,;\n]+/)
      .map((t) => t.trim())
      .filter(Boolean)
    for (const t of tagParts) {
      fd.append('tags', t)
    }

    const result = await uploadSellerAsset(fd)
    if (!result.ok) {
      if (result.fieldErrors) {
        applyApiFieldErrorsToForm(setError, result.fieldErrors)
      }
      toast.error(result.message)
      return
    }

    toast.success('Asset published.')
    reset()
    void queryClient.invalidateQueries({ queryKey: sellerKeys.all })
    void queryClient.invalidateQueries({ queryKey: catalogKeys.all })
    router.push(`/assets/${result.assetId}`)
    router.refresh()
  })

  if (pending) {
    return <SessionBlockSkeleton lines={3} />
  }

  if (!authed) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
        <p className="text-sm text-muted-foreground">Sign in to upload assets.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href="/login?returnUrl=/sell">Sign in</Link>
        </Button>
      </div>
    )
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

      <div className="space-y-1.5">
        <Label htmlFor="upload-tags" className="text-xs font-medium">
          Tags <span className="text-muted-foreground font-normal">(optional)</span>
        </Label>
        <Input
          id="upload-tags"
          className="bg-input border-border"
          placeholder="react, typescript, dashboard"
          {...register('tags')}
        />
        <p className="text-[11px] text-muted-foreground">Comma-separated.</p>
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="upload-file" className="text-xs font-medium">
          Package file
        </Label>
        <Controller
          name="file"
          control={control}
          render={({ field: { onChange, onBlur, name, ref } }) => (
            <input
              id="upload-file"
              ref={ref}
              type="file"
              accept={ASSET_UPLOAD_ALLOWED_EXTENSIONS.join(',')}
              name={name}
              onBlur={onBlur}
              className="sr-only"
              onChange={(e) => {
                const picked = e.target.files?.[0]
                onChange(picked)
                e.target.value = ''
                void trigger('file')
              }}
            />
          )}
        />
        <div className="flex min-h-9 w-full items-center gap-2 rounded-md border border-border bg-input px-3 py-1.5">
          <Button
            type="button"
            variant="secondary"
            className="h-8 shrink-0 px-3 text-xs"
            onClick={() => document.getElementById('upload-file')?.click()}
          >
            Choose file
          </Button>
          <span
            className="min-w-0 flex-1 truncate text-xs text-muted-foreground"
            title={fileDisplayName}
          >
            {fileDisplayName}
          </span>
        </div>
        {errors.file && <p className="text-xs text-destructive">{errors.file.message as string}</p>}
        <p className="text-[11px] text-muted-foreground">
          Max 250 MiB. Supported archives: zip, 7z, rar, tar, tar.gz, tgz.
        </p>
      </div>

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
            Uploading…
          </>
        ) : (
          'Publish asset'
        )}
      </Button>
    </form>
  )
}
