'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'

import { useAuth } from '@/components/auth/auth-context'
import { isEmailVerified } from '@/components/auth/email-verification-notice'
import {
  archiveSellerBundle,
  createSellerBundle,
  restoreSellerBundle,
  reviseSellerBundle,
} from '@/lib/bundles/bundles-api'
import {
  BUNDLE_MAX_ITEMS,
  bundleFormSchema,
  type BundleFormValues,
} from '@/lib/bundles/bundle-schemas'
import {
  bundleKeys,
  fetchSellerBundleQuery,
  fetchSellerBundlesQuery,
} from '@/lib/bundles/bundles-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { fetchSellerListingsQuery, sellerKeys } from '@/lib/seller/seller-query'

export function useSellBundlesController() {
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
    queryFn: ({ signal }) => {
      if (!selectedId) throw new Error('Missing bundle id')
      return fetchSellerBundleQuery(selectedId, signal)
    },
    enabled: authed && Boolean(selectedId) && mode === 'revise',
  })
  const listingsQuery = useQuery({
    queryKey: sellerKeys.listings(),
    queryFn: fetchSellerListingsQuery,
    enabled: authed && verified,
  })

  const detail = detailQuery.data
  const reviseDetail = mode === 'revise' && selectedId && detail?.id === selectedId ? detail : null
  const detailReady = reviseDetail !== null
  const form = useForm<BundleFormValues>({
    resolver: zodResolver(bundleFormSchema),
    defaultValues: { title: '', description: '', price: undefined, assetIds: [] },
  })

  useEffect(() => {
    if (mode !== 'revise' || !detail || detail.id !== selectedId) return
    form.reset({
      title: detail.title,
      description: detail.description ?? '',
      price: detail.price,
      assetIds: detail.items
        .filter((item): item is typeof item & { assetId: string } => item.assetId !== null)
        .sort((left, right) => left.position - right.position)
        .map((item) => item.assetId),
    })
  }, [mode, detail, selectedId, form])

  const watchedAssetIds = useWatch({ control: form.control, name: 'assetIds' }) ?? []
  const watchedPrice = useWatch({ control: form.control, name: 'price' })
  const ownAssets = listingsQuery.data?.items ?? []
  const selectedAssets = ownAssets.filter((asset) => watchedAssetIds.includes(asset.id))
  const listTotal = selectedAssets.reduce((sum, asset) => sum + Number(asset.price), 0)
  const savingsAmount =
    typeof watchedPrice === 'number' && watchedPrice > 0 && listTotal > watchedPrice
      ? listTotal - watchedPrice
      : 0
  const savingsPercent = listTotal > 0 && savingsAmount > 0 ? (savingsAmount / listTotal) * 100 : 0

  const invalidateSeller = () => {
    invalidateQueriesInBackground(queryClient, { queryKey: bundleKeys.all })
  }
  const resetForm = () => form.reset({ title: '', description: '', price: undefined, assetIds: [] })

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
      return mode === 'revise' && selectedId
        ? reviseSellerBundle(selectedId, body)
        : createSellerBundle(body)
    },
    onSuccess: (result) => {
      if (!result.ok) return void toast.error(result.message)
      toast.success(mode === 'revise' ? 'Bundle revision published.' : 'Bundle created.')
      resetForm()
      setSelectedId(result.data.id)
      setMode('revise')
      invalidateSeller()
    },
  })
  const archiveMutation = useMutation({
    mutationFn: archiveSellerBundle,
    onSuccess: (result) => {
      if (!result.ok) return void toast.error(result.message)
      toast.success('Bundle archived.')
      invalidateSeller()
    },
  })
  const restoreMutation = useMutation({
    mutationFn: restoreSellerBundle,
    onSuccess: (result) => {
      if (!result.ok) return void toast.error(result.message)
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
      return
    }
    form.setValue(
      'assetIds',
      current.filter((id) => id !== assetId),
      { shouldDirty: true, shouldValidate: true },
    )
  }

  const moveSelectedAsset = (assetId: string, direction: -1 | 1) => {
    const current = [...(form.getValues('assetIds') ?? [])]
    const index = current.indexOf(assetId)
    const next = index + direction
    if (index < 0 || next < 0 || next >= current.length) return
    const currentId = current[index]
    const adjacentId = current[next]
    if (!currentId || !adjacentId) return
    current[index] = adjacentId
    current[next] = currentId
    form.setValue('assetIds', current, { shouldDirty: true, shouldValidate: true })
  }

  return {
    authed,
    pending,
    verified,
    selectedId,
    setSelectedId,
    mode,
    setMode,
    listQuery,
    detailQuery,
    listingsQuery,
    reviseDetail,
    detailReady,
    detailInitialLoading:
      mode === 'revise' && Boolean(selectedId) && detailQuery.isPending && !detailReady,
    form,
    watchedAssetIds,
    ownAssets,
    selectedAssets,
    listTotal,
    savingsAmount,
    savingsPercent,
    saveMutation,
    archiveMutation,
    restoreMutation,
    toggleAsset,
    moveSelectedAsset,
    resetForm,
  }
}

export type SellBundlesController = ReturnType<typeof useSellBundlesController>
