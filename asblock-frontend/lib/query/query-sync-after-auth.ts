import type { QueryClient } from "@tanstack/react-query";

import { accountKeys, fetchAccountProfile } from "@/lib/account/account-query";
import { authKeys } from "@/lib/auth/auth-query";
import { libraryKeys } from "@/lib/library/library-query";
import { notificationsKeys } from "@/lib/notifications/notifications-query";
import { sellerKeys } from "@/lib/seller/seller-query";

/**
 * Run after successful login/register: refresh session and warm or invalidate user-scoped caches.
 */
export async function syncQueryCacheAfterAuth(queryClient: QueryClient): Promise<void> {
  await queryClient.invalidateQueries({ queryKey: authKeys.session() });
  await queryClient.refetchQueries({ queryKey: authKeys.session() });

  void queryClient.prefetchQuery({
    queryKey: accountKeys.me(),
    queryFn: fetchAccountProfile,
  });

  await Promise.all([
    queryClient.invalidateQueries({ queryKey: sellerKeys.all }),
    queryClient.invalidateQueries({ queryKey: libraryKeys.all }),
    queryClient.invalidateQueries({ queryKey: notificationsKeys.all }),
  ]);
}
