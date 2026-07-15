'use client'

import { AccountSettingsView } from '@/components/account/account-settings-view'
import { useAccountSettings } from '@/lib/account/use-account-settings'

export function AccountSettingsForm() {
  const controller = useAccountSettings()
  return <AccountSettingsView controller={controller} />
}
