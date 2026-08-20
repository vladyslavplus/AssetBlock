'use client'

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import Link from 'next/link'
import { Controller, useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Archive, ArrowDown, ArrowUp, Loader2, Package, Plus, RotateCcw } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import { SellerPriceStepInput } from '@/components/sell/seller-price-step-input'
import {
  archiveSellerBundle,
  createSellerBundle,
  restoreSellerBundle,
  reviseSellerBundle,
} from '@/lib/bundles/bundles-api'
import {
  BUNDLE_MAX_ITEMS,
  BUNDLE_MIN_ITEMS,
  bundleFormSchema,
  type BundleFormValues,
} from '@/lib/bundles/bundle-schemas'
import {
  bundleKeys,
  fetchSellerBundleQuery,
  fetchSellerBundlesQuery,
} from '@/lib/bundles/bundles-query'
import { fetchSellerListingsQuery, sellerKeys } from '@/lib/seller/seller-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { formatUsdWhole } from '@/lib/format-currency'

export function SellMyBundles() {
  const queryClient = useQueryClient()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [mode, setMode] = useState<'create' | 'revise'>('create')

  const listQuery = useQuery({
    queryKey: bundleKeys.sellerList(),
    queryFn: fetchSellerBundlesQuery,
    enabled: authed,
  })

  const detailQuery = useQuery({
    queryKey: bundleKeys.sellerDetail(selectedId ?? ''),
    queryFn: () => {
      if (!selectedId) throw new Error('Missing bundle id')
      return fetchSellerBundleQuery(selectedId)
    },
    enabled: authed && Boolean(selectedId) && mode === 'revise',
  })

  const listingsQuery = useQuery({
    queryKey: sellerKeys.listings(),
    queryFn: fetchSellerListingsQuery,
    enabled: authed && verified,
  })

  const detail = detailQuery.data

  const form = useForm<BundleFormValues>({
    resolver: zodResolver(bundleFormSchema),
    defaultValues: {
      title: '',
      description: '',
      price: undefined,
      assetIds: [],
    },
  })

  useEffect(() => {
    if (mode !== 'revise' || !detail || detail.id !== selectedId) return
    form.reset({
      title: detail.title,
      description: detail.description ?? '',
      price: detail.price,
      assetIds: detail.items
        .filter((i): i is typeof i & { assetId: string } => i.assetId !== null)
        .sort((a, b) => a.position - b.position)
        .map((i) => i.assetId),
    })
  }, [mode, detail, selectedId, form])

  const watchedAssetIds = useWatch({ control: form.control, name: 'assetIds' }) ?? []
  const watchedPrice = useWatch({ control: form.control, name: 'price' })

  const ownAssets = listingsQuery.data?.items ?? []
  const selectedAssets = ownAssets.filter((a) => watchedAssetIds.includes(a.id))
  const listTotal = selectedAssets.reduce((sum, a) => sum + Number(a.price), 0)
  const savingsAmount =
    typeof watchedPrice === 'number' && watchedPrice > 0 && listTotal > watchedPrice
      ? listTotal - watchedPrice
      : 0
  const savingsPercent = listTotal > 0 && savingsAmount > 0 ? (savingsAmount / listTotal) * 100 : 0

  const invalidateSeller = () => {
    invalidateQueriesInBackground(queryClient, { queryKey: bundleKeys.all })
  }

  const saveMutation = useMutation({
    mutationFn: async (values: BundleFormValues) => {
      if (values.price == null) {
        return { ok: false as const, message: 'Price must be greater than zero' }
      }
      const body = {
        title: values.title,
        description: values.description?.trim() ? values.description : null,
        price: values.price,
        assetIds: values.assetIds,
      }
      if (mode === 'revise' && selectedId) {
        return reviseSellerBundle(selectedId, body)
      }
      return createSellerBundle(body)
    },
    onSuccess: (result) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success(mode === 'revise' ? 'Bundle revision published.' : 'Bundle created.')
      form.reset({
        title: '',
        description: '',
        price: undefined,
        assetIds: [],
      })
      setSelectedId(result.data.id)
      setMode('revise')
      invalidateSeller()
    },
  })

  const archiveMutation = useMutation({
    mutationFn: (id: string) => archiveSellerBundle(id),
    onSuccess: (result) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success('Bundle archived.')
      invalidateSeller()
    },
  })

  const restoreMutation = useMutation({
    mutationFn: (id: string) => restoreSellerBundle(id),
    onSuccess: (result) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success('Bundle restored.')
      invalidateSeller()
    },
  })

  const toggleAsset = (assetId: string, checked: boolean) => {
    const current = form.getValues('assetIds') ?? []
    if (checked) {
      if (current.length >= BUNDLE_MAX_ITEMS) {
        toast.error(`Bundles can include at most ${BUNDLE_MAX_ITEMS} assets.`)
        return
      }
      form.setValue('assetIds', [...current, assetId], { shouldDirty: true, shouldValidate: true })
    } else {
      form.setValue(
        'assetIds',
        current.filter((id) => id !== assetId),
        { shouldDirty: true, shouldValidate: true },
      )
    }
  }

  const moveSelectedAsset = (assetId: string, direction: -1 | 1) => {
    const current = [...(form.getValues('assetIds') ?? [])]
    const index = current.indexOf(assetId)
    const next = index + direction
    if (index < 0 || next < 0 || next >= current.length) return
    const left = current[index]
    const right = current[next]
    if (left === undefined || right === undefined) return
    current[index] = right
    current[next] = left
    form.setValue('assetIds', current, { shouldDirty: true, shouldValidate: true })
  }

  if (pending) return <SessionBlockSkeleton />

  if (!authed) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
        <p className="text-sm text-muted-foreground">Sign in to manage bundles.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href="/login?returnUrl=/sell">Sign in</Link>
        </Button>
      </div>
    )
  }

  const items = listQuery.data?.items ?? []

  return (
    <div className="space-y-8">
      {!verified ? <EmailVerificationNotice /> : null}

      {listQuery.isPending ? (
        <p className="text-sm text-muted-foreground">Loading bundles…</p>
      ) : listQuery.isError ? (
        <p className="text-sm text-destructive" role="alert">
          {listQuery.error instanceof Error ? listQuery.error.message : 'Could not load bundles.'}
        </p>
      ) : items.length === 0 && mode !== 'create' ? (
        <div className="rounded-lg border border-dashed border-border px-6 py-12 text-center">
          <Package className="h-10 w-10 text-muted-foreground/50 mx-auto mb-3" aria-hidden />
          <p className="font-medium text-foreground mb-1">No bundles yet</p>
          <p className="text-sm text-muted-foreground mb-4">
            Create a bundle with {BUNDLE_MIN_ITEMS}–{BUNDLE_MAX_ITEMS} of your assets.
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {items.map((b) => (
            <li
              key={b.id}
              className="rounded-lg border border-border bg-card-elevated px-4 py-3 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3"
            >
              <button
                type="button"
                className="text-left min-w-0 flex-1"
                onClick={() => {
                  setSelectedId(b.id)
                  setMode('revise')
                  form.reset({
                    title: b.title,
                    description: b.description ?? '',
                    price: b.price,
                    assetIds: [],
                  })
                }}
              >
                <p className="font-medium text-foreground line-clamp-1">{b.title}</p>
                <p className="text-xs text-muted-foreground mt-0.5 font-mono">
                  {formatUsdWhole(b.price)} · {b.itemCount} assets · rev {b.revisionNumber}
                  {b.isArchived ? ' · archived' : ''}
                </p>
              </button>
              <div className="flex flex-wrap gap-2 shrink-0">
                {!b.isArchived ? (
                  <Button asChild variant="outline" size="sm" className="border-border">
                    <Link href={`/bundles/${b.id}`}>View</Link>
                  </Button>
                ) : null}
                {verified && !b.isArchived ? (
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="border-border"
                    disabled={archiveMutation.isPending}
                    onClick={() => archiveMutation.mutate(b.id)}
                  >
                    <Archive className="size-3.5 mr-1.5" aria-hidden />
                    Archive
                  </Button>
                ) : null}
                {verified && b.isArchived ? (
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="border-border"
                    disabled={restoreMutation.isPending}
                    onClick={() => restoreMutation.mutate(b.id)}
                  >
                    <RotateCcw className="size-3.5 mr-1.5" aria-hidden />
                    Restore
                  </Button>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}

      {verified ? (
        <form
          className="rounded-lg border border-border bg-card-elevated p-4 space-y-4"
          onSubmit={form.handleSubmit((values) => saveMutation.mutate(values))}
        >
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-sm font-semibold text-foreground">
              {mode === 'revise' ? 'Revise bundle' : 'Create bundle'}
            </h2>
            {mode === 'revise' ? (
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="border-border"
                onClick={() => {
                  setMode('create')
                  setSelectedId(null)
                  form.reset({
                    title: '',
                    description: '',
                    price: undefined,
                    assetIds: [],
                  })
                }}
              >
                <Plus className="size-3.5 mr-1.5" aria-hidden />
                New bundle
              </Button>
            ) : null}
          </div>

          {mode === 'revise' && detailQuery.isPending ? (
            <p className="text-xs text-muted-foreground">Loading bundle definition…</p>
          ) : null}

          {mode === 'revise' && detail ? (
            <div className="flex flex-wrap gap-2">
              <Badge variant="secondary" className="text-[10px]">
                Rev {detail.revisionNumber}
              </Badge>
              {detail.isArchived ? (
                <Badge variant="outline" className="text-[10px]">
                  Archived
                </Badge>
              ) : null}
            </div>
          ) : null}

          <div className="space-y-1.5">
            <Label htmlFor="bundle-title">Title</Label>
            <Input id="bundle-title" {...form.register('title')} className="bg-input" />
            {form.formState.errors.title ? (
              <p className="text-xs text-destructive">{form.formState.errors.title.message}</p>
            ) : null}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="bundle-description">Description</Label>
            <Textarea
              id="bundle-description"
              rows={3}
              {...form.register('description')}
              className="bg-input"
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="bundle-price">Bundle price (USD)</Label>
            <Controller
              control={form.control}
              name="price"
              render={({ field }) => (
                <SellerPriceStepInput
                  id="bundle-price"
                  aria-label="Bundle price"
                  value={field.value}
                  onChange={field.onChange}
                  onBlur={field.onBlur}
                />
              )}
            />
            {form.formState.errors.price ? (
              <p className="text-xs text-destructive">{form.formState.errors.price.message}</p>
            ) : null}
          </div>

          <div className="rounded-md border border-border/60 bg-secondary/20 px-3 py-2 space-y-1 text-xs text-muted-foreground">
            <p>
              List total:{' '}
              <span className="font-mono text-foreground">{formatUsdWhole(listTotal)}</span>
            </p>
            {savingsAmount > 0 ? (
              <p className="text-accent font-medium">
                Savings: {formatUsdWhole(savingsAmount)} ({Math.round(savingsPercent)}%)
              </p>
            ) : (
              <p>Set a price below the list total to show savings.</p>
            )}
          </div>

          <div className="space-y-2">
            <Label>
              Assets ({watchedAssetIds.length}/{BUNDLE_MAX_ITEMS}, min {BUNDLE_MIN_ITEMS})
            </Label>
            {form.formState.errors.assetIds ? (
              <p className="text-xs text-destructive">{form.formState.errors.assetIds.message}</p>
            ) : null}
            {ownAssets.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Upload assets first from the Upload asset tab.
              </p>
            ) : (
              <ul className="space-y-2 max-h-64 overflow-y-auto pr-1">
                {ownAssets.map((asset) => {
                  const checked = watchedAssetIds.includes(asset.id)
                  return (
                    <li
                      key={asset.id}
                      className="flex items-start gap-3 rounded-md border border-border/50 px-3 py-2"
                    >
                      <Checkbox
                        id={`bundle-asset-${asset.id}`}
                        checked={checked}
                        onCheckedChange={(v) => toggleAsset(asset.id, v === true)}
                        aria-label={`Include ${asset.title}`}
                      />
                      <label
                        htmlFor={`bundle-asset-${asset.id}`}
                        className="min-w-0 flex-1 cursor-pointer"
                      >
                        <span className="text-sm font-medium text-foreground line-clamp-1">
                          {asset.title}
                        </span>
                        <span className="block text-[11px] font-mono text-muted-foreground">
                          {formatUsdWhole(Number(asset.price))}
                        </span>
                      </label>
                    </li>
                  )
                })}
              </ul>
            )}
            {watchedAssetIds.length > 0 ? (
              <div className="space-y-2 pt-2 border-t border-border/40">
                <p className="text-xs text-muted-foreground">Bundle order</p>
                <ul className="space-y-2">
                  {watchedAssetIds.map((id, index) => {
                    const asset = ownAssets.find((a) => a.id === id)
                    if (!asset) return null
                    return (
                      <li
                        key={id}
                        className="flex items-center gap-2 rounded-md border border-border/50 px-3 py-2"
                      >
                        <span className="text-xs font-mono text-muted-foreground w-5">
                          {index + 1}.
                        </span>
                        <span className="text-sm text-foreground flex-1 line-clamp-1">
                          {asset.title}
                        </span>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          className="border-border h-8 w-8 p-0"
                          disabled={index === 0}
                          aria-label={`Move ${asset.title} up`}
                          onClick={() => moveSelectedAsset(id, -1)}
                        >
                          <ArrowUp className="size-3.5" />
                        </Button>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          className="border-border h-8 w-8 p-0"
                          disabled={index === watchedAssetIds.length - 1}
                          aria-label={`Move ${asset.title} down`}
                          onClick={() => moveSelectedAsset(id, 1)}
                        >
                          <ArrowDown className="size-3.5" />
                        </Button>
                      </li>
                    )
                  })}
                </ul>
              </div>
            ) : null}
          </div>

          <Button
            type="submit"
            disabled={saveMutation.isPending || (mode === 'revise' && detail?.isArchived)}
            className="bg-primary text-primary-foreground hover:bg-[#6D28D9]"
          >
            {saveMutation.isPending ? (
              <Loader2 className="size-4 animate-spin mr-2" aria-hidden />
            ) : null}
            {mode === 'revise' ? 'Publish revision' : 'Create bundle'}
          </Button>
        </form>
      ) : null}
    </div>
  )
}
