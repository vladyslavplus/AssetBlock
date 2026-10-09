'use client'

import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Controller, useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { AlertCircle, Loader2, RefreshCw } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { AssetLicenseSelector } from '@/components/assets/asset-license-selector'
import { applyApiFieldErrorsToForm } from '@/lib/http/api-errors'
import {
  ASSET_UPLOAD_ALLOWED_EXTENSIONS,
  publishVersionFormSchema,
  type PublishVersionFormValues,
} from '@/lib/seller/seller-schemas'
import { publishSellerAssetVersion } from '@/lib/seller/seller-api'
import { fetchSellerDraftQuery, sellerKeys } from '@/lib/seller/seller-query'
import { sellerProcessingKeys } from '@/lib/seller/seller-processing-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'

interface PublishVersionFormProps {
  assetId: string
  blockers?: string[]
}

export function PublishVersionForm({ assetId, blockers = [] }: PublishVersionFormProps) {
  const queryClient = useQueryClient()
  const [stale, setStale] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const blocked = blockers.length > 0

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
    if (blocked) return
    setStale(false)
    setFailure(null)

    let draft
    try {
      draft = await fetchSellerDraftQuery({ assetId })
    } catch {
      setFailure('Could not load the saved draft. Your selected file is kept — try again.')
      return
    }

    const fd = new FormData()
    fd.set('licenseCode', values.licenseCode)
    fd.set('releaseNotes', values.releaseNotes.trim())
    fd.set('file', values.file)
    fd.set('workspaceId', draft.workspaceId)
    fd.set('expectedWorkspaceRevision', String(draft.workspaceRevision))

    let result
    try {
      result = await publishSellerAssetVersion(assetId, fd)
    } catch {
      setFailure(
        'The upload outcome could not be confirmed. Refresh version history before uploading again.',
      )
      return
    }
    if (!result.ok) {
      if (result.code === 'ERR_MODERATION_WORKSPACE_STALE') {
        setStale(true)
        toast.error('The draft changed while you were working. Refresh and try again.')
        return
      }
      if (result.fieldErrors) {
        applyApiFieldErrorsToForm(setError, result.fieldErrors)
      }
      setFailure(result.message)
      toast.error(result.message)
      return
    }

    toast.success('New version uploaded. Security processing started.')
    reset({ licenseCode: values.licenseCode, releaseNotes: '' })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.versions(assetId) })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.draft(assetId) })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.all })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerProcessingKeys.asset(assetId) })
    invalidateQueriesInBackground(queryClient, { queryKey: sellerProcessingKeys.all })
  })

  const refreshWorkspace = async () => {
    setStale(false)
    await Promise.all([
      queryClient.refetchQueries({ queryKey: sellerKeys.draft(assetId) }),
      queryClient.refetchQueries({ queryKey: sellerKeys.declaration(assetId, null) }),
      queryClient.refetchQueries({ queryKey: sellerKeys.versions(assetId) }),
    ])
  }

  return (
    <form
      onSubmit={onSubmit}
      className="space-y-4 rounded-lg border border-border bg-card-elevated/30 p-4"
    >
      <div>
        <h3 className="text-sm font-semibold text-foreground">Upload new version</h3>
        <p className="text-xs text-muted-foreground mt-1">
          Uploading a package starts security processing. It does not publish the version — buyers
          get entitled newer versions only after an approved review.
        </p>
      </div>

      {blocked && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-3">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-xs text-amber-800 dark:text-amber-200">
            Unsaved changes in: {blockers.join(', ')}. Save them before uploading so the new version
            is created from the saved data you see.
          </AlertDescription>
        </Alert>
      )}

      {stale && (
        <Alert className="border-amber-500/40 bg-amber-500/10 py-3">
          <AlertCircle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
          <AlertDescription className="text-xs text-amber-800 dark:text-amber-200 flex flex-wrap items-center gap-2">
            The draft changed while you were working. Your selected file is kept — refresh to load
            the latest draft, then upload again. The newer draft is not overwritten automatically.
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => void refreshWorkspace()}
            >
              <RefreshCw className="mr-1.5 size-3.5" aria-hidden />
              Refresh
            </Button>
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

      <Alert className="border-border bg-card-elevated/40 py-3">
        <AlertDescription className="text-[11px] text-muted-foreground space-y-1">
          <p>
            Before upload: the package is encrypted and stored, and technical checks (archive
            inspection and malware scan) run automatically.
          </p>
          <p>
            A limited, audited reviewer comparison of this code may be introduced later as part of
            moderation. Seller code is not used for model training by default.
          </p>
          <p>
            Saved source declarations stay linked to the draft and uploaded version. Automatic
            expiry is not available yet. Deleting a listing does not remove existing buyers&apos;
            access to their purchased version.
          </p>
        </AlertDescription>
      </Alert>

      <Button
        type="submit"
        disabled={isSubmitting || blocked}
        className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
      >
        {isSubmitting ? (
          <>
            <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
            Uploading…
          </>
        ) : (
          'Upload version'
        )}
      </Button>
    </form>
  )
}
