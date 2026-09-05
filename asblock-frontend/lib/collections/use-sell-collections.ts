'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'

import { isEmailVerified } from '@/components/auth/email-verification-notice'
import { useAuth } from '@/components/auth/auth-context'
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
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { fetchSellerListingsQuery, sellerKeys } from '@/lib/seller/seller-query'

export function useSellCollectionsController() {
  const queryClient = useQueryClient()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [addAssetId, setAddAssetId] = useState('')

  const listQuery = useQuery({
    queryKey: collectionKeys.sellerList(),
    queryFn: () => fetchSellerCollectionsQuery(),
    enabled: authed,
  })
  const detailQuery = useQuery({
    queryKey: collectionKeys.sellerDetail(selectedId ?? ''),
    queryFn: () => {
      if (!selectedId) throw new Error('Missing collection id')
      return fetchSellerCollectionQuery(selectedId)
    },
    enabled: authed && Boolean(selectedId),
  })
  const listingsQuery = useQuery({
    queryKey: sellerKeys.listings(),
    queryFn: () => fetchSellerListingsQuery(),
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
      if (!result.ok) return void toast.error(result.message)
      toast.success('Collection created as draft.')
      createForm.reset({ title: '', description: '' })
      setSelectedId(result.id)
      invalidateSeller()
    },
  })
  const updateMutation = useMutation({
    mutationFn: async (values: CollectionMetadataFormValues): Promise<SellerMutationResult> => {
      if (!selectedId) return { ok: false, message: 'No collection selected.' }
      return updateSellerCollection(selectedId, {
        title: values.title,
        description: values.description?.trim() ? values.description : null,
      })
    },
    onSuccess: (result) => {
      if (!result.ok) return void toast.error(result.message)
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
      if (!result.ok) return void toast.error(result.message)
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
      if (!result.ok) return void toast.error(result.message)
      if (op.type === 'add') {
        toast.success('Asset added.')
        setAddAssetId('')
      } else if (op.type === 'remove') {
        toast.success('Asset removed.')
      }
      invalidateSeller()
    },
  })

  const items = listQuery.data?.items ?? []
  const detail = detailQuery.data
  const managedDetail = selectedId && detail?.id === selectedId ? detail : null
  const orderedItems = managedDetail
    ? [...managedDetail.items].sort((left, right) => left.position - right.position)
    : []
  const memberIds = new Set(orderedItems.map((item) => item.assetId))
  const ownAssets = (listingsQuery.data?.items ?? []).filter((asset) => !memberIds.has(asset.id))

  const moveItem = (assetId: string, direction: -1 | 1) => {
    const ids = orderedItems.map((item) => item.assetId)
    const index = ids.indexOf(assetId)
    const next = index + direction
    if (index < 0 || next < 0 || next >= ids.length) return
    const nextIds = [...ids]
    const currentId = nextIds[index]
    const adjacentId = nextIds[next]
    if (!currentId || !adjacentId) return
    nextIds[index] = adjacentId
    nextIds[next] = currentId
    itemMutation.mutate({ type: 'reorder', assetIds: nextIds, assetId })
  }

  return {
    authed,
    pending,
    verified,
    selectedId,
    setSelectedId,
    addAssetId,
    setAddAssetId,
    listQuery,
    detailQuery,
    listingsQuery,
    createForm,
    editForm,
    createMutation,
    updateMutation,
    actionMutation,
    itemMutation,
    items,
    managedDetail,
    detailReady: managedDetail !== null,
    detailInitialLoading: Boolean(selectedId) && detailQuery.isPending && managedDetail === null,
    orderedItems,
    ownAssets,
    moveItem,
  }
}

export type SellCollectionsController = ReturnType<typeof useSellCollectionsController>
