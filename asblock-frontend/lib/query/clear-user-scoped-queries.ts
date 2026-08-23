import type { QueryClient } from '@tanstack/react-query'

import { accountKeys } from '@/lib/account/account-query'
import { bundleKeys } from '@/lib/bundles/bundles-query'
import { collectionKeys } from '@/lib/collections/collections-query'
import { libraryKeys } from '@/lib/library/library-query'
import { notificationsKeys } from '@/lib/notifications/notifications-query'
import { sellerKeys } from '@/lib/seller/seller-query'

/**
 * Drops private seller/library/account/notification cache without touching `auth.session`.
 * Removing the session query here would refetch it and can loop after logout/session loss.
 */
export function clearPrivateUserQueries(queryClient: QueryClient): void {
  queryClient.removeQueries({ queryKey: sellerKeys.all })
  queryClient.removeQueries({ queryKey: libraryKeys.all })
  queryClient.removeQueries({ queryKey: notificationsKeys.all })
  queryClient.removeQueries({ queryKey: accountKeys.all })
  queryClient.removeQueries({ queryKey: [...collectionKeys.all, 'seller'] })
  queryClient.removeQueries({ queryKey: [...bundleKeys.all, 'seller'] })
}
