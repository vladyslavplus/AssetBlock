'use client'

import { ArrowLeft } from 'lucide-react'

import { AccountPasswordForm } from '@/components/account/account-password-form'
import { AccountProfileForm } from '@/components/account/account-profile-form'
import { AccountProfileCardSkeleton } from '@/components/skeletons/account-settings-skeleton'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import type { AccountSettingsController } from '@/lib/account/use-account-settings'

interface AccountSettingsViewProps {
  controller: AccountSettingsController
}

export function AccountSettingsView({ controller }: AccountSettingsViewProps) {
  const { profile, profileQuery, section, openPasswordSection, backToProfileSection } = controller

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
            onClick={() => void profileQuery.refetch()}
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
              <CardDescription>
                Update how you appear on AssetBlock. Email is read-only; contact support to change
                it.
              </CardDescription>
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="shrink-0 self-start"
              onClick={openPasswordSection}
            >
              Change password
            </Button>
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
            <CardTitle className="text-xl">Password</CardTitle>
            <CardDescription>Use a strong password you do not reuse elsewhere.</CardDescription>
          </div>
        </CardHeader>
      )}

      <CardContent>
        {section === 'profile' ? (
          <AccountProfileForm controller={controller} />
        ) : (
          <AccountPasswordForm controller={controller} />
        )}
      </CardContent>
    </Card>
  )
}
