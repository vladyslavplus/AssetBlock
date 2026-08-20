'use client'

import { useQueryClient } from '@tanstack/react-query'
import { Controller, useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Loader2 } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { AssetLicenseSelector } from '@/components/assets/asset-license-selector'
import { applyApiFieldErrorsToForm } from '@/lib/http/api-errors'
import { assetKeys } from '@/lib/catalog/asset-detail-query'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { libraryKeys } from '@/lib/library/library-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import {
  ASSET_UPLOAD_ALLOWED_EXTENSIONS,
  publishVersionFormSchema,
  type PublishVersionFormValues,
} from '@/lib/seller/seller-schemas'
import { publishSellerAssetVersion } from '@/lib/seller/seller-api'
import { sellerKeys } from '@/lib/seller/seller-query'

interface PublishVersionFormProps {
  assetId: string
}

export function PublishVersionForm({ assetId }: PublishVersionFormProps) {
  const queryClient = useQueryClient()

  const {
    register,
    control,
    setError,
    trigger,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<PublishVersionFormValues>({
    resolver: zodResolver(publishVersionFormSchema),
    defaultValues: {
      licenseCode: 'PERSONAL',
      releaseNotes: '',
    },
  })

  const selectedFile = useWatch({ control, name: 'file' })
  const fileDisplayName =
    selectedFile instanceof File && selectedFile.name.length > 0
      ? selectedFile.name
      : 'No file chosen'

  const onSubmit = handleSubmit(async (values) => {
    const fd = new FormData()
    fd.set('licenseCode', values.licenseCode)
    fd.set('releaseNotes', values.releaseNotes.trim())
    fd.set('file', values.file)

    const result = await publishSellerAssetVersion(assetId, fd)
    if (!result.ok) {
      if (result.fieldErrors) {
        applyApiFieldErrorsToForm(setError, result.fieldErrors)
      }
      toast.error(result.message)
      return
    }

    toast.success('New version published.')
    reset({ licenseCode: values.licenseCode, releaseNotes: '' })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.versions(assetId) })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.all })
    invalidateQueriesInBackground(queryClient, { queryKey: assetKeys.detail(assetId) })
    invalidateQueriesInBackground(queryClient, { queryKey: assetKeys.versions(assetId) })
    invalidateQueriesInBackground(queryClient, { queryKey: catalogKeys.all })
    invalidateQueriesInBackground(queryClient, { queryKey: libraryKeys.all })
  })

  return (
    <form
      onSubmit={onSubmit}
      className="space-y-4 rounded-lg border border-border bg-card-elevated/30 p-4"
    >
      <div>
        <h3 className="text-sm font-semibold text-foreground">Publish new version</h3>
        <p className="text-xs text-muted-foreground mt-1">
          Upload an updated package. Existing buyers can download entitled newer versions at no
          extra charge.
        </p>
      </div>

      <AssetLicenseSelector
        control={control}
        name="licenseCode"
        errors={errors}
        idPrefix="publish-version"
      />

      <div className="space-y-1.5">
        <Label htmlFor="publish-release-notes" className="text-xs font-medium">
          Release notes
        </Label>
        <Textarea
          id="publish-release-notes"
          className="bg-input border-border h-24"
          placeholder="What changed in this version?"
          {...register('releaseNotes')}
        />
        {errors.releaseNotes ? (
          <p className="text-xs text-destructive">{errors.releaseNotes.message}</p>
        ) : null}
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="publish-version-file" className="text-xs font-medium">
          Package file
        </Label>
        <Controller
          name="file"
          control={control}
          render={({ field: { onChange, onBlur, name, ref } }) => (
            <input
              id="publish-version-file"
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
            onClick={() => document.getElementById('publish-version-file')?.click()}
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
        {errors.file ? (
          <p className="text-xs text-destructive">{errors.file.message as string}</p>
        ) : null}
      </div>

      <Button
        type="submit"
        disabled={isSubmitting}
        className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
      >
        {isSubmitting ? (
          <>
            <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
            Publishing…
          </>
        ) : (
          'Publish version'
        )}
      </Button>
    </form>
  )
}
