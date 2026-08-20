'use client'

import { ArrowLeft } from 'lucide-react'

import { AccountEmailForm } from '@/components/account/account-email-form'
import { AccountPasswordForm } from '@/components/account/account-password-form'
import { AccountProfileForm } from '@/components/account/account-profile-form'
import { AccountProfileCardSkeleton } from '@/components/skeletons/account-settings-skeleton'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import type { AccountSettingsController } from '@/lib/account/use-account-settings'
import { runQueryInBackground } from '@/lib/query/query-refresh'

interface AccountSettingsViewProps {
  controller: AccountSettingsController
}

export function AccountSettingsView({ controller }: AccountSettingsViewProps) {
  const {
    profile,
    profileQuery,
    section,
    openPasswordSection,
    openEmailSection,
    backToProfileSection,
  } = controller

  if (profileQuery.isPending && !profile) {
    return <AccountProfileCardSkeleton />
  }

  if (profileQuery.isError && !profile) {
    return (
      <Card className="border-border bg-card-elevated">
        <CardHeader>
          <CardTitle className="text-xl">Account</CardTitle>
          <CardDescription>We could not load your profile.</CardDescription>
        </CardHeader>
        <CardContent>
          <p className="text-destructive text-sm">
            {profileQuery.error instanceof Error
              ? profileQuery.error.message
              : 'Network error. Try again.'}
          </p>
          <Button
            type="button"
            variant="outline"
            className="mt-4"
            onClick={() => runQueryInBackground(profileQuery.refetch({ cancelRefetch: false }))}
          >
            Retry
          </Button>
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className="border-border bg-card-elevated">
      {section === 'profile' ? (
        <CardHeader className="space-y-4">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div className="space-y-1.5">
              <CardTitle className="text-xl">Profile</CardTitle>
              <CardDescription>Update how you appear on AssetBlock.</CardDescription>
            </div>
            <div className="flex flex-wrap gap-2 shrink-0 self-start">
              <Button type="button" variant="outline" size="sm" onClick={openEmailSection}>
                Change email
              </Button>
              <Button type="button" variant="outline" size="sm" onClick={openPasswordSection}>
                Change password
              </Button>
            </div>
          </div>
        </CardHeader>
      ) : section === 'password' ? (
        <CardHeader className="space-y-4 pb-2">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="-ml-2 h-8 w-fit gap-1.5 px-2 text-muted-foreground hover:text-foreground"
            onClick={backToProfileSection}
          >
            <ArrowLeft className="size-4 shrink-0" aria-hidden />
            Back to profile
          </Button>
          <div className="space-y-1.5">
            <CardTitle className="text-xl">Password</CardTitle>
            <CardDescription>Use a strong password you do not reuse elsewhere.</CardDescription>
          </div>
        </CardHeader>
      ) : (
        <CardHeader className="space-y-4 pb-2">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="-ml-2 h-8 w-fit gap-1.5 px-2 text-muted-foreground hover:text-foreground"
            onClick={backToProfileSection}
          >
            <ArrowLeft className="size-4 shrink-0" aria-hidden />
            Back to profile
          </Button>
          <div className="space-y-1.5">
            <CardTitle className="text-xl">Change email</CardTitle>
            <CardDescription>
              Request an email address change. A confirmation link will be sent to the new address.
            </CardDescription>
          </div>
        </CardHeader>
      )}

      <CardContent>
        {section === 'profile' ? (
          <AccountProfileForm controller={controller} />
        ) : section === 'password' ? (
          <AccountPasswordForm controller={controller} />
        ) : (
          <AccountEmailForm controller={controller} />
        )}
      </CardContent>
    </Card>
  )
}
