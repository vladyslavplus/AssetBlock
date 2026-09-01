'use client'

import Link from 'next/link'
import { Controller } from 'react-hook-form'
import { Archive, ArrowDown, ArrowUp, Loader2, Package, Plus, RotateCcw } from 'lucide-react'
import { routes } from '@/lib/routes'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { EmailVerificationNotice } from '@/components/auth/email-verification-notice'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import { SellerPriceStepInput } from '@/components/sell/seller-price-step-input'
import {
  SellAssetChecklistSkeleton,
  SellBundleListSkeleton,
  SellFormSkeleton,
} from '@/components/sell/sell-panel-skeletons'
import { SellQueryError } from '@/components/sell/sell-query-error'
import { BUNDLE_MAX_ITEMS, BUNDLE_MIN_ITEMS } from '@/lib/bundles/bundle-schemas'
import { runQueryInBackground } from '@/lib/query/query-refresh'
import { formatUsdWhole } from '@/lib/format-currency'
import {
  useSellBundlesController,
  type SellBundlesController,
} from '@/lib/bundles/use-sell-bundles'

export function SellMyBundles() {
  const controller = useSellBundlesController()
  return <SellMyBundlesView controller={controller} />
}

function SellMyBundlesView({ controller }: { controller: SellBundlesController }) {
  const {
    authed,
    pending,
    verified,
    setSelectedId,
    mode,
    setMode,
    listQuery,
    detailQuery,
    listingsQuery,
    reviseDetail,
    detailReady,
    detailInitialLoading,
    form,
    watchedAssetIds,
    ownAssets,
    listTotal,
    savingsAmount,
    savingsPercent,
    saveMutation,
    archiveMutation,
    restoreMutation,
    toggleAsset,
    moveSelectedAsset,
    resetForm,
  } = controller

  if (pending) return <SessionBlockSkeleton />

  if (!authed) {
    return (
      <div className="max-w-lg w-full rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
        <p className="text-sm text-muted-foreground">Sign in to manage bundles.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href={routes.login(routes.sell())}>Sign in</Link>
        </Button>
      </div>
    )
  }

  const items = listQuery.data?.items ?? []
  const listingsPending = listingsQuery.isPending
  const titleInvalid = Boolean(form.formState.errors.title)
  const descriptionInvalid = Boolean(form.formState.errors.description)
  const priceInvalid = Boolean(form.formState.errors.price)
  const assetsInvalid = Boolean(form.formState.errors.assetIds)
  const showFormFields = mode === 'create' || detailReady
  const archivingId = archiveMutation.isPending ? archiveMutation.variables : null
  const restoringId = restoreMutation.isPending ? restoreMutation.variables : null

  return (
    <div className="max-w-lg w-full space-y-8">
      {!verified ? <EmailVerificationNotice /> : null}

      {listQuery.isPending ? (
        <SellBundleListSkeleton />
      ) : listQuery.isError ? (
        <SellQueryError
          title="Could not load bundles."
          onRetry={() => runQueryInBackground(listQuery.refetch())}
          retrying={listQuery.isRefetching}
        />
      ) : items.length === 0 ? (
        <div className="rounded-lg border border-dashed border-border px-6 py-12 text-center">
          <Package className="h-10 w-10 text-muted-foreground/50 mx-auto mb-3" aria-hidden />
          <p className="font-medium text-foreground mb-1">No bundles yet</p>
          <p className="text-sm text-muted-foreground mb-4">
            Create a bundle with {BUNDLE_MIN_ITEMS}–{BUNDLE_MAX_ITEMS} of your assets.
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {items.map((b) => {
            const isArchiving = archivingId === b.id
            const isRestoring = restoringId === b.id
            return (
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
                      <Link href={routes.bundleDetail(b.id)}>View</Link>
                    </Button>
                  ) : null}
                  {verified && !b.isArchived ? (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="border-border"
                      disabled={archiveMutation.isPending || restoreMutation.isPending}
                      onClick={() => archiveMutation.mutate(b.id)}
                    >
                      {isArchiving ? (
                        <>
                          <Loader2 className="size-3.5 mr-1.5 animate-spin" aria-hidden />
                          Archiving…
                        </>
                      ) : (
                        <>
                          <Archive className="size-3.5 mr-1.5" aria-hidden />
                          Archive
                        </>
                      )}
                    </Button>
                  ) : null}
                  {verified && b.isArchived ? (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="border-border"
                      disabled={archiveMutation.isPending || restoreMutation.isPending}
                      onClick={() => restoreMutation.mutate(b.id)}
                    >
                      {isRestoring ? (
                        <>
                          <Loader2 className="size-3.5 mr-1.5 animate-spin" aria-hidden />
                          Restoring…
                        </>
                      ) : (
                        <>
                          <RotateCcw className="size-3.5 mr-1.5" aria-hidden />
                          Restore
                        </>
                      )}
                    </Button>
                  ) : null}
                </div>
              </li>
            )
          })}
        </ul>
      )}

      {verified ? (
        <div className="space-y-5">
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
                  resetForm()
                }}
              >
                <Plus className="size-3.5 mr-1.5" aria-hidden />
                New bundle
              </Button>
            ) : null}
          </div>

          {detailInitialLoading ? (
            <SellFormSkeleton fields={4} label="Loading bundle definition" />
          ) : null}

          {mode === 'revise' && detailQuery.isError && !detailReady ? (
            <SellQueryError
              title="Could not load this bundle."
              onRetry={() => runQueryInBackground(detailQuery.refetch())}
              retrying={detailQuery.isRefetching}
            />
          ) : null}

          {showFormFields ? (
            <form
              className="space-y-5"
              onSubmit={form.handleSubmit((values) => saveMutation.mutate(values))}
            >
              {reviseDetail ? (
                <div className="flex flex-wrap gap-2">
                  <Badge variant="secondary" className="text-[10px]">
                    Rev {reviseDetail.revisionNumber}
                  </Badge>
                  {reviseDetail.isArchived ? (
                    <Badge variant="outline" className="text-[10px]">
                      Archived
                    </Badge>
                  ) : null}
                </div>
              ) : null}

              <div className="space-y-1.5">
                <Label htmlFor="bundle-title" className="text-xs font-medium">
                  Title
                </Label>
                <Input
                  id="bundle-title"
                  className="bg-input border-border"
                  aria-invalid={titleInvalid || undefined}
                  {...form.register('title')}
                />
                {form.formState.errors.title ? (
                  <p className="text-xs text-destructive">{form.formState.errors.title.message}</p>
                ) : null}
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="bundle-description" className="text-xs font-medium">
                  Description <span className="text-muted-foreground font-normal">(optional)</span>
                </Label>
                <Textarea
                  id="bundle-description"
                  className="bg-input border-border h-44 sm:h-40 md:h-36"
                  aria-invalid={descriptionInvalid || undefined}
                  {...form.register('description')}
                />
                {form.formState.errors.description ? (
                  <p className="text-xs text-destructive">
                    {form.formState.errors.description.message}
                  </p>
                ) : null}
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="bundle-price" className="text-xs font-medium">
                  Bundle price (USD)
                </Label>
                <Controller
                  control={form.control}
                  name="price"
                  render={({ field }) => (
                    <SellerPriceStepInput
                      id="bundle-price"
                      aria-label="Bundle price"
                      aria-invalid={priceInvalid || undefined}
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

              <div className="rounded-md border border-border/60 bg-secondary/20 px-3 py-2 space-y-1 text-[11px] text-muted-foreground">
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
                <Label className="text-xs font-medium">
                  Assets ({watchedAssetIds.length}/{BUNDLE_MAX_ITEMS}, min {BUNDLE_MIN_ITEMS})
                </Label>
                {form.formState.errors.assetIds ? (
                  <p className="text-xs text-destructive" role="alert">
                    {typeof form.formState.errors.assetIds.message === 'string'
                      ? form.formState.errors.assetIds.message
                      : 'Select valid assets for this bundle.'}
                  </p>
                ) : null}
                {listingsPending ? (
                  <SellAssetChecklistSkeleton />
                ) : listingsQuery.isError ? (
                  <SellQueryError
                    title="Could not load your assets."
                    onRetry={() => runQueryInBackground(listingsQuery.refetch())}
                    retrying={listingsQuery.isRefetching}
                  />
                ) : ownAssets.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-border px-4 py-6 text-center">
                    <p className="text-sm text-muted-foreground">
                      Upload assets first from the Upload asset tab.
                    </p>
                  </div>
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
                            aria-invalid={assetsInvalid || undefined}
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
                    <p className="text-[11px] text-muted-foreground">Bundle order</p>
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
                disabled={
                  saveMutation.isPending ||
                  listingsPending ||
                  listingsQuery.isError ||
                  (mode === 'revise' && reviseDetail?.isArchived)
                }
                className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
              >
                {saveMutation.isPending ? (
                  <>
                    <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
                    {mode === 'revise' ? 'Publishing revision…' : 'Creating…'}
                  </>
                ) : mode === 'revise' ? (
                  'Publish revision'
                ) : (
                  'Create bundle'
                )}
              </Button>
            </form>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}
