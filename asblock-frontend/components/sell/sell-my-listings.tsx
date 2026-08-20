'use client'

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { ExternalLink, Loader2, Package, Pencil, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { useAuth } from '@/components/auth/auth-context'
import {
  EmailVerificationNotice,
  isEmailVerified,
} from '@/components/auth/email-verification-notice'
import { SessionBlockSkeleton } from '@/components/skeletons/session-block-skeleton'
import { SellListingListSkeleton } from '@/components/sell/sell-listing-row-skeleton'
import { deleteSellerAsset } from '@/lib/seller/seller-api'
import { formatUsdWhole } from '@/lib/format-currency'
import type { AssetListItemApi } from '@/lib/catalog/assets-api'
import { catalogKeys } from '@/lib/catalog/catalog-query'
import { fetchSellerListingsQuery, sellerKeys } from '@/lib/seller/seller-query'
import { invalidateQueriesInBackground } from '@/lib/query/query-refresh'
import { useState } from 'react'

export function SellMyListings() {
  const router = useRouter()
  const queryClient = useQueryClient()
  const { status, user } = useAuth()
  const authed = status === 'authenticated'
  const pending = status === 'loading'
  const verified = isEmailVerified(user)

  const listingsQuery = useQuery({
    queryKey: sellerKeys.listings(),
    queryFn: fetchSellerListingsQuery,
    enabled: authed,
  })

  const deleteMutation = useMutation({
    mutationFn: deleteSellerAsset,
    onSuccess: (result) => {
      if (!result.ok) {
        toast.error(result.message)
        return
      }
      toast.success('Asset removed.')
      setDeleteTarget(null)
      invalidateQueriesInBackground(queryClient, { queryKey: sellerKeys.all })
      invalidateQueriesInBackground(queryClient, { queryKey: catalogKeys.all })
      router.refresh()
    },
  })

  const [deleteTarget, setDeleteTarget] = useState<{ id: string; title: string } | null>(null)

  const items: AssetListItemApi[] = listingsQuery.data?.items ?? []
  const loading = authed && listingsQuery.isPending
  const error =
    listingsQuery.error instanceof Error
      ? listingsQuery.error.message === 'SIGN_IN_REQUIRED'
        ? 'Please sign in to view your listings.'
        : listingsQuery.error.message
      : listingsQuery.isError
        ? 'Could not load listings.'
        : null

  async function confirmDeleteListing() {
    if (!deleteTarget) return
    deleteMutation.mutate(deleteTarget.id)
  }

  if (pending) {
    return <SessionBlockSkeleton />
  }

  if (!authed) {
    return (
      <div className="rounded-lg border border-border bg-card-elevated/50 px-4 py-8 text-center space-y-3">
        <p className="text-sm text-muted-foreground">Sign in to see assets you have published.</p>
        <Button asChild className="bg-primary text-primary-foreground hover:bg-[#6D28D9]">
          <Link href="/login?returnUrl=/sell">Sign in</Link>
        </Button>
      </div>
    )
  }

  if (loading) {
    return (
      <div className="py-4">
        <SellListingListSkeleton rows={5} />
      </div>
    )
  }

  if (error) {
    return (
      <p className="text-sm text-destructive py-4" role="alert">
        {error}
      </p>
    )
  }

  if (items.length === 0) {
    return (
      <div className="rounded-lg border border-dashed border-border px-6 py-12 text-center">
        <Package className="h-10 w-10 text-muted-foreground/50 mx-auto mb-3" aria-hidden />
        <p className="font-medium text-foreground mb-1">No listings yet</p>
        <p className="text-sm text-muted-foreground mb-4">
          Upload your first asset using the <span className="text-foreground">Upload asset</span>{' '}
          tab.
        </p>
        <Button asChild variant="outline" className="border-border">
          <Link href="/assets">Browse the catalog for inspiration</Link>
        </Button>
      </div>
    )
  }

  const deletingId = deleteMutation.isPending ? (deleteTarget?.id ?? null) : null

  return (
    <>
      {!verified ? <EmailVerificationNotice className="mb-4" /> : null}
      <ul className="space-y-3">
        {items.map((a) => (
          <li
            key={a.id}
            className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 rounded-lg border border-border bg-card-elevated px-4 py-3"
          >
            <div className="min-w-0">
              <p className="font-medium text-foreground line-clamp-2">{a.title}</p>
              <p className="text-xs text-muted-foreground mt-0.5 font-mono tabular-nums">
                {formatUsdWhole(Number(a.price))}
                {a.categoryName ? (
                  <span className="text-muted-foreground/80"> · {a.categoryName}</span>
                ) : null}
              </p>
            </div>
            <div className="flex flex-wrap gap-2 shrink-0">
              <Button asChild variant="outline" size="sm" className="border-border">
                <Link href={`/assets/${a.id}`}>
                  <ExternalLink className="h-3.5 w-3.5 mr-1.5" />
                  View
                </Link>
              </Button>
              {verified ? (
                <>
                  <Button asChild variant="outline" size="sm" className="border-border">
                    <Link href={`/sell/assets/${a.id}/edit`}>
                      <Pencil className="h-3.5 w-3.5 mr-1.5" />
                      Edit
                    </Link>
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                    disabled={deleteMutation.isPending}
                    onClick={() => setDeleteTarget({ id: a.id, title: a.title })}
                  >
                    {deletingId === a.id ? (
                      <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden />
                    ) : (
                      <Trash2 className="h-3.5 w-3.5 mr-1.5" aria-hidden />
                    )}
                    Delete
                  </Button>
                </>
              ) : (
                <Button asChild size="sm" variant="outline" className="border-amber-500/40">
                  <Link href="/account">Verify to manage</Link>
                </Button>
              )}
            </div>
          </li>
        ))}
      </ul>

      <AlertDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => !open && setDeleteTarget(null)}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete this listing?</AlertDialogTitle>
            <AlertDialogDescription>
              <span className="block">
                This permanently removes{' '}
                <span className="font-medium text-foreground">
                  &quot;{deleteTarget?.title}&quot;
                </span>{' '}
                from the marketplace. This cannot be undone here.
              </span>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={deleteMutation.isPending}>Cancel</AlertDialogCancel>
            <Button
              type="button"
              variant="destructive"
              disabled={deleteMutation.isPending}
              onClick={() => void confirmDeleteListing()}
            >
              {deleteMutation.isPending ? (
                <>
                  <Loader2 className="h-4 w-4 mr-2 animate-spin inline" aria-hidden />
                  Deleting…
                </>
              ) : (
                'Delete permanently'
              )}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}
