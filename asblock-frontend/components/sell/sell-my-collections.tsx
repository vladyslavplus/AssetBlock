'use client'

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import Link from 'next/link'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import {
  Archive,
  ArrowDown,
  ArrowUp,
  FolderOpen,
  Loader2,
  Plus,
  RotateCcw,
  Trash2,
} from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import {
  SellCollectionListSkeleton,
  SellCollectionManagementSkeleton,
  SellSelectControlSkeleton,
} from '@/components/sell/sell-panel-skeletons'
import { SellQueryError } from '@/components/sell/sell-query-error'
import {
  addSellerCollectionItem,
  archiveSellerCollection,
  createSellerCollection,
  publishSellerCollection,
  removeSellerCollectionItem,
  reorderSellerCollectionItems,
  restoreSellerCollection,
  updateSellerCollection,
  type SellerMutationResult,
} from '@/lib/collections/collections-api'
import {
  collectionMetadataFormSchema,
  type CollectionMetadataFormValues,
} from '@/lib/collections/collection-schemas'
import {
  collectionKeys,
  fetchSellerCollectionQuery,
  fetchSellerCollectionsQuery,
} from '@/lib/collections/collections-query'
import { fetchSellerListingsQuery, sellerKeys } from '@/lib/seller/seller-query'
import { invalidateQueriesInBackground, runQueryInBackground } from '@/lib/query/query-refresh'
import { formatUsdWhole } from '@/lib/format-currency'

function statusBadgeVariant(status: string): 'default' | 'secondary' | 'outline' {
  if (status === 'PUBLISHED') return 'default'
  if (status === 'ARCHIVED') return 'outline'
  return 'secondary'
}

export function SellMyCollections() {
  const queryClient = useQueryClient()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [addAssetId, setAddAssetId] = useState<string>('')

  const listQuery = useQuery({
    queryKey: collectionKeys.sellerList(),
    queryFn: fetchSellerCollectionsQuery,
    enabled: authed,
  })

  const detailQuery = useQuery({
    queryKey: collectionKeys.sellerDetail(selectedId ?? ''),
    queryFn: ({ signal }) => {
      if (!selectedId) throw new Error('Missing collection id')
      return fetchSellerCollectionQuery(selectedId, signal)
    },
    enabled: authed && Boolean(selectedId),
  })

  const listingsQuery = useQuery({
    queryKey: sellerKeys.listings(),
    queryFn: fetchSellerListingsQuery,
    enabled: authed && verified,
  })

  const createForm = useForm<CollectionMetadataFormValues>({
    resolver: zodResolver(collectionMetadataFormSchema),
    defaultValues: { title: '', description: '' },
  })

  const editForm = useForm<CollectionMetadataFormValues>({
    resolver: zodResolver(collectionMetadataFormSchema),
    defaultValues: { title: '', description: '' },
  })

  useEffect(() => {
    if (!detailQuery.data || detailQuery.data.id !== selectedId) return
    editForm.reset({
      title: detailQuery.data.title,
      description: detailQuery.data.description ?? '',
    })
  }, [detailQuery.data, selectedId, editForm])

  const invalidateSeller = () => {
    invalidateQueriesInBackground(queryClient, { queryKey: collectionKeys.all })
  }

  const createMutation = useMutation({
    mutationFn: createSellerCollection,
    onSuccess: (result) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success('Collection created as draft.')
      createForm.reset({ title: '', description: '' })
      setSelectedId(result.id)
      invalidateSeller()
    },
  })

  const updateMutation = useMutation({
    mutationFn: async (values: CollectionMetadataFormValues): Promise<SellerMutationResult> => {
      if (!selectedId) {
        return { ok: false, message: 'No collection selected.' }
      }
      return updateSellerCollection(selectedId, {
        title: values.title,
        description: values.description?.trim() ? values.description : null,
      })
    },
    onSuccess: (result) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success('Collection updated.')
      invalidateSeller()
    },
  })

  const actionMutation = useMutation({
    mutationFn: async (
      action: 'publish' | 'archive' | 'restore',
    ): Promise<SellerMutationResult> => {
      if (!selectedId) return { ok: false, message: 'No collection selected.' }
      if (action === 'publish') return publishSellerCollection(selectedId)
      if (action === 'archive') return archiveSellerCollection(selectedId)
      return restoreSellerCollection(selectedId)
    },
    onSuccess: (result, action) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success(
        action === 'publish'
          ? 'Collection published.'
          : action === 'archive'
            ? 'Collection archived.'
            : 'Collection restored to draft.',
      )
      invalidateSeller()
    },
  })

  const itemMutation = useMutation({
    mutationFn: async (op: {
      type: 'add' | 'remove' | 'reorder'
      assetId?: string
      assetIds?: string[]
    }) => {
      if (!selectedId) return { ok: false as const, message: 'No collection selected.' }
      if (op.type === 'add' && op.assetId) {
        return addSellerCollectionItem(selectedId, { assetId: op.assetId })
      }
      if (op.type === 'remove' && op.assetId) {
        return removeSellerCollectionItem(selectedId, op.assetId)
      }
      if (op.type === 'reorder' && op.assetIds) {
        return reorderSellerCollectionItems(selectedId, { assetIds: op.assetIds })
      }
      return { ok: false as const, message: 'Invalid operation.' }
    },
    onSuccess: (result, op) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      if (op.type === 'add') {
        toast.success('Asset added.')
        setAddAssetId('')
      } else if (op.type === 'remove') {
        toast.success('Asset removed.')
      }
      invalidateSeller()
    },
  })

  if (pending) return <SessionBlockSkeleton />

  if (!authed) {
    return (
      <div className="max-w-lg w-full rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
        <p className="text-sm text-muted-foreground">Sign in to manage collections.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href="/login?returnUrl=/sell">Sign in</Link>
        </Button>
      </div>
    )
  }

  const items = listQuery.data?.items ?? []
  const detail = detailQuery.data
  const managedDetail = selectedId && detail && detail.id === selectedId ? detail : null
  const detailReady = managedDetail != null
  const detailInitialLoading = Boolean(selectedId) && detailQuery.isPending && !detailReady
  const orderedItems = managedDetail
    ? [...managedDetail.items].sort((a, b) => a.position - b.position)
    : []
  const memberIds = new Set(orderedItems.map((i) => i.assetId))
  const listingsPending = listingsQuery.isPending
  const ownAssets = (listingsQuery.data?.items ?? []).filter((a) => !memberIds.has(a.id))
  const pendingAction = actionMutation.isPending ? actionMutation.variables : null
  const pendingItem = itemMutation.isPending ? itemMutation.variables : null

  const moveItem = (assetId: string, direction: -1 | 1) => {
    const ids = orderedItems.map((i) => i.assetId)
    const index = ids.indexOf(assetId)
    const next = index + direction
    if (index < 0 || next < 0 || next >= ids.length) return
    const nextIds = [...ids]
    const left = nextIds[index]
    const right = nextIds[next]
    if (left === undefined || right === undefined) return
    nextIds[index] = right
    nextIds[next] = left
    itemMutation.mutate({ type: 'reorder', assetIds: nextIds, assetId })
  }

  const createTitleInvalid = Boolean(createForm.formState.errors.title)
  const createDescriptionInvalid = Boolean(createForm.formState.errors.description)
  const editTitleInvalid = Boolean(editForm.formState.errors.title)
  const editDescriptionInvalid = Boolean(editForm.formState.errors.description)

  return (
    <div className="max-w-lg w-full space-y-8">
      {!verified ? <EmailVerificationNotice /> : null}

      {verified ? (
        <form
          className="space-y-5"
          onSubmit={createForm.handleSubmit((values) =>
            createMutation.mutate({
              title: values.title,
              description: values.description?.trim() ? values.description : null,
            }),
          )}
        >
          <h2 className="text-sm font-semibold text-foreground">Create collection</h2>
          <div className="space-y-1.5">
            <Label htmlFor="collection-title" className="text-xs font-medium">
              Title
            </Label>
            <Input
              id="collection-title"
              className="bg-input border-border"
              aria-invalid={createTitleInvalid || undefined}
              {...createForm.register('title')}
            />
            {createForm.formState.errors.title ? (
              <p className="text-xs text-destructive">
                {createForm.formState.errors.title.message}
              </p>
            ) : null}
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="collection-description" className="text-xs font-medium">
              Description <span className="text-muted-foreground font-normal">(optional)</span>
            </Label>
            <Textarea
              id="collection-description"
              className="bg-input border-border h-44 sm:h-40 md:h-36"
              aria-invalid={createDescriptionInvalid || undefined}
              {...createForm.register('description')}
            />
            {createForm.formState.errors.description ? (
              <p className="text-xs text-destructive">
                {createForm.formState.errors.description.message}
              </p>
            ) : null}
          </div>
          <Button
            type="submit"
            disabled={createMutation.isPending}
            className="bg-primary text-primary-foreground hover:bg-[#6D28D9] w-full sm:w-auto"
          >
            {createMutation.isPending ? (
              <>
                <Loader2 className="h-4 w-4 mr-2 animate-spin" aria-hidden />
                Creating…
              </>
            ) : (
              <>
                <Plus className="h-4 w-4 mr-2" aria-hidden />
                Create draft
              </>
            )}
          </Button>
        </form>
      ) : null}

      {listQuery.isPending ? (
        <SellCollectionListSkeleton />
      ) : listQuery.isError ? (
        <SellQueryError
          title="Could not load collections."
          onRetry={() => runQueryInBackground(listQuery.refetch())}
          retrying={listQuery.isRefetching}
        />
      ) : items.length === 0 ? (
        <div className="rounded-lg border border-dashed border-border px-6 py-12 text-center">
          <FolderOpen className="h-10 w-10 text-muted-foreground/50 mx-auto mb-3" aria-hidden />
          <p className="font-medium text-foreground mb-1">No collections yet</p>
          <p className="text-sm text-muted-foreground">
            Create a draft collection and add your published assets.
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {items.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                onClick={() => setSelectedId(c.id)}
                className={`w-full text-left rounded-lg border px-4 py-3 transition-colors ${
                  selectedId === c.id
                    ? 'border-primary/50 bg-card-elevated'
                    : 'border-border bg-card-elevated/40 hover:bg-card-elevated'
                }`}
              >
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <p className="font-medium text-foreground line-clamp-1">{c.title}</p>
                    <p className="text-xs text-muted-foreground mt-0.5">
                      {c.itemCount} {c.itemCount === 1 ? 'item' : 'items'}
                    </p>
                  </div>
                  <Badge variant={statusBadgeVariant(c.status)} className="text-[10px] shrink-0">
                    {c.status}
                  </Badge>
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}

      {detailInitialLoading ? <SellCollectionManagementSkeleton /> : null}

      {selectedId && detailQuery.isError && !detailReady ? (
        <SellQueryError
          title="Could not load this collection."
          onRetry={() => runQueryInBackground(detailQuery.refetch())}
          retrying={detailQuery.isRefetching}
        />
      ) : null}

      {managedDetail ? (
        <div className="rounded-lg border border-border bg-card-elevated p-4 space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-sm font-semibold text-foreground">Manage collection</h2>
            <Badge variant={statusBadgeVariant(managedDetail.status)} className="text-[10px]">
              {managedDetail.status}
            </Badge>
          </div>

          {verified ? (
            <form
              className="space-y-5"
              onSubmit={editForm.handleSubmit((values) => updateMutation.mutate(values))}
            >
              <div className="space-y-1.5">
                <Label htmlFor="edit-collection-title" className="text-xs font-medium">
                  Title
                </Label>
                <Input
                  id="edit-collection-title"
                  className="bg-input border-border"
                  aria-invalid={editTitleInvalid || undefined}
                  {...editForm.register('title')}
                />
                {editForm.formState.errors.title ? (
                  <p className="text-xs text-destructive">
                    {editForm.formState.errors.title.message}
                  </p>
                ) : null}
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="edit-collection-description" className="text-xs font-medium">
                  Description <span className="text-muted-foreground font-normal">(optional)</span>
                </Label>
                <Textarea
                  id="edit-collection-description"
                  className="bg-input border-border h-44 sm:h-40 md:h-36"
                  aria-invalid={editDescriptionInvalid || undefined}
                  {...editForm.register('description')}
                />
                {editForm.formState.errors.description ? (
                  <p className="text-xs text-destructive">
                    {editForm.formState.errors.description.message}
                  </p>
                ) : null}
              </div>
              <div className="flex flex-wrap gap-2">
                <Button
                  type="submit"
                  variant="outline"
                  size="sm"
                  disabled={updateMutation.isPending}
                  className="border-border"
                >
                  {updateMutation.isPending ? (
                    <>
                      <Loader2 className="size-3.5 mr-1.5 animate-spin" aria-hidden />
                      Saving…
                    </>
                  ) : (
                    'Save metadata'
                  )}
                </Button>
                {managedDetail.status === 'DRAFT' ? (
                  <Button
                    type="button"
                    size="sm"
                    className="bg-primary text-primary-foreground hover:bg-[#6D28D9]"
                    disabled={actionMutation.isPending}
                    onClick={() => actionMutation.mutate('publish')}
                  >
                    {pendingAction === 'publish' ? (
                      <>
                        <Loader2 className="size-3.5 mr-1.5 animate-spin" aria-hidden />
                        Publishing…
                      </>
                    ) : (
                      'Publish'
                    )}
                  </Button>
                ) : null}
                {managedDetail.status === 'PUBLISHED' ? (
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="border-border"
                    disabled={actionMutation.isPending}
                    onClick={() => actionMutation.mutate('archive')}
                  >
                    {pendingAction === 'archive' ? (
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
                {managedDetail.status === 'ARCHIVED' ? (
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="border-border"
                    disabled={actionMutation.isPending}
                    onClick={() => actionMutation.mutate('restore')}
                  >
                    {pendingAction === 'restore' ? (
                      <>
                        <Loader2 className="size-3.5 mr-1.5 animate-spin" aria-hidden />
                        Restoring…
                      </>
                    ) : (
                      <>
                        <RotateCcw className="size-3.5 mr-1.5" aria-hidden />
                        Restore to draft
                      </>
                    )}
                  </Button>
                ) : null}
                {managedDetail.status === 'PUBLISHED' ? (
                  <Button asChild variant="outline" size="sm" className="border-border">
                    <Link href={`/collections/${managedDetail.id}`}>View public page</Link>
                  </Button>
                ) : null}
              </div>
            </form>
          ) : null}

          <div className="space-y-2 border-t border-border/50 pt-4">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
              Items
            </h3>
            {orderedItems.length === 0 ? (
              <p className="text-sm text-muted-foreground">No assets in this collection yet.</p>
            ) : (
              <ul className="space-y-2">
                {orderedItems.map((item, index) => {
                  const rowBusy =
                    pendingItem?.type === 'remove' && pendingItem.assetId === item.assetId
                  const rowReorderBusy =
                    pendingItem?.type === 'reorder' && pendingItem.assetId === item.assetId
                  return (
                    <li
                      key={item.assetId}
                      className="flex flex-col sm:flex-row sm:items-center gap-2 rounded-md border border-border/60 px-3 py-2"
                    >
                      <div className="min-w-0 flex-1">
                        <p className="text-sm font-medium text-foreground line-clamp-1">
                          {item.title}
                        </p>
                        <p className="text-[11px] text-muted-foreground font-mono">
                          {formatUsdWhole(item.price)}
                          {!item.isAvailable ? ' · unavailable' : ''}
                        </p>
                      </div>
                      {verified && managedDetail.status !== 'ARCHIVED' ? (
                        <div className="flex gap-1 shrink-0">
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            className="border-border h-8 w-8 p-0"
                            disabled={index === 0 || itemMutation.isPending}
                            aria-label={`Move ${item.title} up`}
                            onClick={() => moveItem(item.assetId, -1)}
                          >
                            {rowReorderBusy ? (
                              <Loader2 className="size-3.5 animate-spin" aria-hidden />
                            ) : (
                              <ArrowUp className="size-3.5" />
                            )}
                          </Button>
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            className="border-border h-8 w-8 p-0"
                            disabled={index === orderedItems.length - 1 || itemMutation.isPending}
                            aria-label={`Move ${item.title} down`}
                            onClick={() => moveItem(item.assetId, 1)}
                          >
                            {rowReorderBusy ? (
                              <Loader2 className="size-3.5 animate-spin" aria-hidden />
                            ) : (
                              <ArrowDown className="size-3.5" />
                            )}
                          </Button>
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            className="border-destructive/40 text-destructive h-8 w-8 p-0"
                            disabled={itemMutation.isPending}
                            aria-label={`Remove ${item.title}`}
                            onClick={() =>
                              itemMutation.mutate({ type: 'remove', assetId: item.assetId })
                            }
                          >
                            {rowBusy ? (
                              <Loader2 className="size-3.5 animate-spin" aria-hidden />
                            ) : (
                              <Trash2 className="size-3.5" />
                            )}
                          </Button>
                        </div>
                      ) : null}
                    </li>
                  )
                })}
              </ul>
            )}

            {verified && managedDetail.status !== 'ARCHIVED' ? (
              <div className="flex flex-col sm:flex-row gap-2 pt-2">
                {listingsPending ? (
                  <div className="flex-1 min-w-0">
                    <SellSelectControlSkeleton label="Loading assets" />
                  </div>
                ) : listingsQuery.isError ? (
                  <div className="flex-1 min-w-0">
                    <SellQueryError
                      title="Could not load your assets."
                      onRetry={() => runQueryInBackground(listingsQuery.refetch())}
                      retrying={listingsQuery.isRefetching}
                    />
                  </div>
                ) : (
                  <Select
                    value={addAssetId || undefined}
                    onValueChange={setAddAssetId}
                    disabled={ownAssets.length === 0}
                  >
                    <SelectTrigger
                      className="bg-input border-border flex-1 min-w-0"
                      aria-label="Add own asset"
                    >
                      <SelectValue
                        placeholder={
                          ownAssets.length === 0
                            ? 'No available assets to add'
                            : 'Add an asset you own…'
                        }
                      />
                    </SelectTrigger>
                    <SelectContent>
                      {ownAssets.map((a) => (
                        <SelectItem key={a.id} value={a.id}>
                          {a.title}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
                <Button
                  type="button"
                  variant="outline"
                  className="border-border shrink-0"
                  disabled={
                    listingsPending ||
                    listingsQuery.isError ||
                    !addAssetId ||
                    ownAssets.length === 0 ||
                    itemMutation.isPending
                  }
                  onClick={() => itemMutation.mutate({ type: 'add', assetId: addAssetId })}
                >
                  {pendingItem?.type === 'add' ? (
                    <>
                      <Loader2 className="size-3.5 mr-1.5 animate-spin" aria-hidden />
                      Adding…
                    </>
                  ) : (
                    'Add item'
                  )}
                </Button>
              </div>
            ) : null}
          </div>
        </div>
      ) : null}
    </div>
  )
}
