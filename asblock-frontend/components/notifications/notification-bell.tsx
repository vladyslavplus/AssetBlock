'use client'

import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
  type InfiniteData,
} from '@tanstack/react-query'
import { formatDistanceToNow } from 'date-fns'
import { Bell, BellOff, Loader2, RotateCw } from 'lucide-react'
import Link from 'next/link'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'

import { useAuth } from '@/components/auth/auth-context'
import {
  NotificationListSkeleton,
  NotificationListSkeletonRow,
} from '@/components/notifications/notification-list-skeleton'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { getApiErrorMessage } from '@/lib/http/api-errors'
import type {
  NotificationListItem,
  PagedNotificationsDto,
} from '@/lib/notifications/notification-types'
import { subscribeNotificationHub } from '@/lib/notifications/notification-hub'
import {
  getNotificationAssetId,
  getNotificationBody,
  getNotificationTitle,
} from '@/lib/notifications/notification-ui'
import {
  fetchNotificationsPage,
  fetchNotificationsUnreadCount,
  NOTIFICATIONS_PAGE_SIZE,
  notificationsKeys,
  patchNotificationRead,
  patchNotificationUnread,
  postMarkAllNotificationsRead,
} from '@/lib/notifications/notifications-query'
import { cn } from '@/lib/utils'

function updateInboxItemReadAt(
  old: InfiniteData<PagedNotificationsDto, number> | undefined,
  id: string,
  readAt: string | null,
): InfiniteData<PagedNotificationsDto, number> | undefined {
  if (!old) {
    return old
  }
  return {
    ...old,
    pages: old.pages.map((page) => ({
      ...page,
      items: page.items.map((item) => (item.id === id ? { ...item, readAt } : item)),
    })),
  }
}

function markAllInboxItemsRead(
  old: InfiniteData<PagedNotificationsDto, number> | undefined,
  readAt: string,
): InfiniteData<PagedNotificationsDto, number> | undefined {
  if (!old) {
    return old
  }
  return {
    ...old,
    pages: old.pages.map((page) => ({
      ...page,
      items: page.items.map((item) => (item.readAt ? item : { ...item, readAt })),
    })),
  }
}

export function NotificationBell() {
  const { status } = useAuth()
  const [open, setOpen] = useState(false)
  const queryClient = useQueryClient()

  const unreadQuery = useQuery({
    queryKey: notificationsKeys.unread(),
    queryFn: fetchNotificationsUnreadCount,
    enabled: status === 'authenticated',
  })

  const inboxQuery = useInfiniteQuery({
    queryKey: notificationsKeys.inbox(),
    queryFn: ({ pageParam }) => fetchNotificationsPage(pageParam, NOTIFICATIONS_PAGE_SIZE),
    initialPageParam: 1,
    getNextPageParam: (lastPage, allPages) => {
      const batch = lastPage.items ?? []
      const total = Number(lastPage.totalCount) || 0
      const loaded = allPages.reduce((acc, p) => acc + (p.items?.length ?? 0), 0)
      if (loaded >= total || batch.length < NOTIFICATIONS_PAGE_SIZE) {
        return undefined
      }
      return allPages.length + 1
    },
    enabled: status === 'authenticated' && open,
  })

  const items = inboxQuery.data?.pages.flatMap((page) => page.items ?? []) ?? []

  const unreadCount = unreadQuery.data ?? 0

  const refreshMutation = useMutation({
    mutationFn: async () => {
      await queryClient.resetQueries({ queryKey: notificationsKeys.inbox(), exact: true })
      await queryClient.invalidateQueries({ queryKey: notificationsKeys.unread() })
    },
    onError: () => {
      toast.error('Could not refresh notifications.')
    },
  })

  const readAllMutation = useMutation({
    mutationFn: postMarkAllNotificationsRead,
    onSuccess: (data) => {
      const now = new Date().toISOString()
      queryClient.setQueryData<InfiniteData<PagedNotificationsDto, number>>(
        notificationsKeys.inbox(),
        (old) => markAllInboxItemsRead(old, now),
      )
      toast.success(
        data.updatedCount > 0
          ? `Marked ${data.updatedCount} notification${data.updatedCount === 1 ? '' : 's'} as read.`
          : 'Nothing unread.',
      )
      void queryClient.invalidateQueries({ queryKey: notificationsKeys.unread() })
      void queryClient.invalidateQueries({ queryKey: notificationsKeys.inbox() })
    },
    onError: (err: unknown) => {
      const msg =
        err instanceof Error ? err.message : getApiErrorMessage(err, 'Could not mark all as read.')
      toast.error(msg)
    },
  })

  useEffect(() => {
    if (status !== 'authenticated') {
      return
    }
    return subscribeNotificationHub(() => {
      void queryClient.invalidateQueries({ queryKey: notificationsKeys.all })
    })
  }, [status, queryClient])

  const toggleRead = async (n: NotificationListItem) => {
    const wasUnread = !n.readAt
    try {
      if (wasUnread) {
        await patchNotificationRead(n.id)
        queryClient.setQueryData<InfiniteData<PagedNotificationsDto, number>>(
          notificationsKeys.inbox(),
          (old) => updateInboxItemReadAt(old, n.id, new Date().toISOString()),
        )
      } else {
        await patchNotificationUnread(n.id)
        queryClient.setQueryData<InfiniteData<PagedNotificationsDto, number>>(
          notificationsKeys.inbox(),
          (old) => updateInboxItemReadAt(old, n.id, null),
        )
      }
      void queryClient.invalidateQueries({ queryKey: notificationsKeys.unread() })
    } catch (err) {
      toast.error(
        err instanceof Error
          ? err.message
          : wasUnread
            ? 'Could not mark as read.'
            : 'Could not mark as unread.',
      )
    }
  }

  if (status !== 'authenticated') {
    return null
  }

  const badge = unreadCount > 99 ? '99+' : unreadCount > 0 ? String(unreadCount) : null

  const listPending = inboxQuery.isPending
  const listError = inboxQuery.isError
  const skeletonRows =
    listPending && items.length === 0
      ? NOTIFICATIONS_PAGE_SIZE
      : Math.min(items.length, NOTIFICATIONS_PAGE_SIZE)

  const showLoadMore = Boolean(inboxQuery.hasNextPage) && !listPending
  const canReadAll = unreadCount > 0 && !readAllMutation.isPending
  const listBusy = refreshMutation.isPending
  const listBodyScrollable = items.length > 0 || (listPending && items.length === 0)

  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <DropdownMenuTrigger asChild>
        <Button
          type="button"
          variant="outline"
          size="icon"
          className="relative border-border text-foreground bg-transparent hover:bg-secondary/50 shrink-0"
          aria-label={badge ? `Notifications, ${unreadCount} unread` : 'Notifications'}
        >
          <Bell className="size-4" aria-hidden />
          {badge ? (
            <span className="absolute -top-1 -right-1 min-w-[1rem] h-4 px-1 rounded-full bg-primary text-[10px] font-semibold text-primary-foreground flex items-center justify-center tabular-nums">
              {badge}
            </span>
          ) : null}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        className="w-[min(100vw-2rem,22rem)] p-0 border-border bg-card text-card-foreground flex max-h-[min(var(--radix-dropdown-menu-content-available-height),calc(100vh-2rem))] flex-col overflow-hidden"
      >
        <div className="shrink-0 px-3 py-2 border-b border-border flex items-center justify-between gap-2">
          <span className="text-sm font-semibold text-foreground shrink-0">Notifications</span>
          <div className="flex items-center gap-0.5 shrink-0">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="h-8 px-2 text-xs text-muted-foreground hover:text-foreground"
              disabled={!canReadAll}
              onClick={() => readAllMutation.mutate()}
            >
              {readAllMutation.isPending ? 'Marking…' : 'Read all'}
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="icon"
              className="h-8 w-8 text-muted-foreground hover:text-foreground"
              disabled={listBusy}
              aria-label="Refresh notifications"
              onClick={() => refreshMutation.mutate()}
            >
              {listBusy ? (
                <Loader2 className="size-4 animate-spin" aria-hidden />
              ) : (
                <RotateCw className="size-4" aria-hidden />
              )}
            </Button>
          </div>
        </div>

        <div
          className={cn(
            'min-w-0 overflow-y-auto overscroll-contain scrollbar-themed',
            listBodyScrollable && 'min-h-0 flex-1',
          )}
        >
          {listPending && items.length === 0 ? (
            <NotificationListSkeleton rows={skeletonRows} />
          ) : listError && items.length === 0 ? (
            <p className="px-3 py-4 text-xs text-destructive">
              Could not load notifications. Try Refresh or check your connection.
            </p>
          ) : items.length === 0 ? (
            <div className="flex flex-col items-center justify-center gap-2 px-4 py-8 text-center">
              <BellOff className="size-8 text-muted-foreground/50" aria-hidden />
              <p className="text-sm font-medium text-foreground">No notifications yet</p>
              <p className="text-xs text-muted-foreground leading-relaxed max-w-[18rem]">
                Purchases, downloads, sales, and reviews will show up here. We will notify you in
                real time when something new arrives.
              </p>
            </div>
          ) : (
            <ul className="divide-y divide-border">
              {items.map((n) => {
                const title = getNotificationTitle(n.kind)
                const body = getNotificationBody(n.kind, n.metadataJson)
                const assetId = getNotificationAssetId(n.metadataJson)
                const unread = !n.readAt
                return (
                  <li key={n.id}>
                    <button
                      type="button"
                      className={cn(
                        'w-full text-left px-3 py-2.5 hover:bg-secondary/50 transition-colors',
                        unread && 'bg-primary/5',
                      )}
                      onClick={() => void toggleRead(n)}
                      title={unread ? 'Mark as read' : 'Mark as unread'}
                    >
                      <div className="flex items-start justify-between gap-2">
                        <span
                          className={cn(
                            'text-xs font-medium text-foreground',
                            unread && 'font-semibold',
                          )}
                        >
                          {title}
                        </span>
                        <span className="text-[10px] text-muted-foreground shrink-0 tabular-nums">
                          {formatDistanceToNow(new Date(n.createdAt), { addSuffix: true })}
                        </span>
                      </div>
                      {body ? (
                        <p className="text-xs text-muted-foreground mt-0.5 line-clamp-2">{body}</p>
                      ) : null}
                      {assetId ? (
                        <Link
                          href={`/assets/${assetId}`}
                          className="text-xs text-accent mt-1 inline-block hover:underline"
                          onClick={(e) => e.stopPropagation()}
                        >
                          View asset
                        </Link>
                      ) : null}
                    </button>
                  </li>
                )
              })}
              {inboxQuery.isFetchingNextPage ? <NotificationListSkeletonRow /> : null}
            </ul>
          )}
        </div>

        {showLoadMore ? (
          <div className="shrink-0 px-3 py-2 border-t border-border flex flex-col items-center gap-1.5">
            <button
              type="button"
              className="text-xs font-medium text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary rounded-sm"
              onClick={() => void inboxQuery.fetchNextPage()}
              disabled={inboxQuery.isFetchingNextPage}
            >
              {inboxQuery.isFetchingNextPage ? 'Loading…' : 'Load more'}
            </button>
          </div>
        ) : null}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
