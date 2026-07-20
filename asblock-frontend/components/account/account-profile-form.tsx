'use client'

import { BadgeCheck, Clock, MailWarning } from 'lucide-react'

import { EmailVerificationNotice } from '@/components/auth/email-verification-notice'
import { SocialLinksFieldsSkeleton } from '@/components/skeletons/account-settings-skeleton'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Textarea } from '@/components/ui/textarea'
import type { AccountSettingsController } from '@/lib/account/use-account-settings'

interface AccountProfileFormProps {
  controller: AccountSettingsController
}

export function AccountProfileForm({ controller }: AccountProfileFormProps) {
  const {
    profile,
    socialPlatforms,
    socialPlatformsLoading,
    socialPlatformsError,
    socialUrls,
    setSocialUrl,
    profileForm,
    profileFields,
    profileValues,
    savingProfile,
    hasProfileOrSocialChanges,
    saveBlockedBySocial,
    resendingVerification,
    resendCooldown,
    onSaveProfile,
    onCancelProfile,
    onResendVerification,
  } = controller

  const isVerified = Boolean(profile?.emailVerifiedAt)
  const profileLocked = !isVerified
  const hasPendingChange = Boolean(profile?.pendingEmail)

  return (
    <form className="space-y-4" onSubmit={onSaveProfile} noValidate>
      {profileLocked ? <EmailVerificationNotice /> : null}

      <div className="space-y-2">
        <Label htmlFor="account-email">Email</Label>
        <Input
          id="account-email"
          value={profile?.email == null ? '' : String(profile.email)}
          readOnly
          disabled
          className="bg-muted/50"
        />
        <div className="flex flex-wrap items-center gap-2">
          {isVerified ? (
            <span className="inline-flex items-center gap-1 text-xs text-green-500 font-medium">
              <BadgeCheck className="size-3.5 shrink-0" aria-hidden />
              Verified
            </span>
          ) : (
            <>
              <span className="inline-flex items-center gap-1 text-xs text-amber-500 font-medium">
                <MailWarning className="size-3.5 shrink-0" aria-hidden />
                Verification pending
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-6 px-2 text-xs"
                disabled={resendingVerification || resendCooldown}
                onClick={onResendVerification}
              >
                {resendingVerification
                  ? 'Sending…'
                  : resendCooldown
                    ? 'Email sent'
                    : 'Resend verification'}
              </Button>
            </>
          )}
        </div>
        {hasPendingChange && (
          <div className="flex items-start gap-1.5 rounded-md border border-border/60 bg-muted/30 px-3 py-2">
            <Clock className="size-3.5 mt-0.5 shrink-0 text-muted-foreground" aria-hidden />
            <p className="text-xs text-muted-foreground">
              Email change to <strong className="text-foreground">{profile?.pendingEmail}</strong>{' '}
              is pending confirmation. Check your new inbox for the link.
            </p>
          </div>
        )}
      </div>

      <div className="space-y-2">
        <Label htmlFor="account-username">Username</Label>
        <Input
          id="account-username"
          autoComplete="username"
          disabled={profileLocked}
          {...profileFields.username}
          value={profileValues.username ?? ''}
        />
        {profileForm.formState.errors.username ? (
          <p className="text-destructive text-sm">
            {profileForm.formState.errors.username.message}
          </p>
        ) : null}
      </div>

      <div className="space-y-2">
        <Label htmlFor="account-bio">Bio</Label>
        <Textarea
          id="account-bio"
          className="bg-input border-border"
          disabled={profileLocked}
          {...profileFields.bio}
          value={profileValues.bio ?? ''}
        />
        {profileForm.formState.errors.bio ? (
          <p className="text-destructive text-sm">{profileForm.formState.errors.bio.message}</p>
        ) : null}
      </div>

      <div className="space-y-2">
        <Label htmlFor="account-avatar">Avatar URL</Label>
        <Input
          id="account-avatar"
          type="url"
          placeholder="https://…"
          disabled={profileLocked}
          {...profileFields.avatarUrl}
          value={profileValues.avatarUrl ?? ''}
        />
        {profileForm.formState.errors.avatarUrl ? (
          <p className="text-destructive text-sm">
            {profileForm.formState.errors.avatarUrl.message}
          </p>
        ) : null}
      </div>

      <div className="flex items-center justify-between gap-4 rounded-lg border p-3">
        <div>
          <p className="text-sm font-medium">Public profile</p>
          <p className="text-muted-foreground text-xs">Allow others to view your profile page.</p>
        </div>
        <Switch
          checked={profileValues.isPublicProfile ?? true}
          disabled={profileLocked}
          onCheckedChange={(value) =>
            profileForm.setValue('isPublicProfile', value, { shouldDirty: true })
          }
        />
      </div>

      <div className="space-y-4 border-t border-border/60 pt-6">
        <div>
          <p className="text-sm font-medium text-foreground">Social links</p>
          <p className="text-muted-foreground text-xs mt-0.5">
            Shown on your public profile. Leave blank to remove a link. Must be full{' '}
            <span className="font-mono">http://</span> or{' '}
            <span className="font-mono">https://</span> URLs.
          </p>
        </div>
        {socialPlatformsLoading ? <SocialLinksFieldsSkeleton rows={4} /> : null}
        {socialPlatformsError ? (
          <p className="text-destructive text-sm" role="alert">
            {socialPlatformsError}
          </p>
        ) : null}
        {!socialPlatformsLoading &&
        !socialPlatformsError &&
        socialPlatforms.length === 0 &&
        profile ? (
          <p className="text-sm text-muted-foreground">No social platforms are available.</p>
        ) : null}
        <div className="space-y-3">
          {[...socialPlatforms]
            .sort((left, right) => left.name.localeCompare(right.name))
            .map((platform) => (
              <div key={platform.id} className="space-y-1.5">
                <Label htmlFor={`social-${platform.id}`} className="text-xs font-medium">
                  {platform.name}
                </Label>
                <Input
                  id={`social-${platform.id}`}
                  type="url"
                  placeholder="https://…"
                  autoComplete="off"
                  value={socialUrls[platform.id] ?? ''}
                  onChange={(event) => setSocialUrl(platform.id, event.target.value)}
                  disabled={profileLocked || socialPlatformsLoading || savingProfile}
                  className="bg-input border-border"
                />
              </div>
            ))}
        </div>
      </div>

      <div className="flex flex-wrap gap-2">
        <Button
          type="submit"
          disabled={
            profileLocked || savingProfile || !hasProfileOrSocialChanges || saveBlockedBySocial
          }
        >
          {savingProfile ? 'Saving…' : 'Save changes'}
        </Button>
        <Button
          type="button"
          variant="outline"
          onClick={onCancelProfile}
          disabled={profileLocked || !hasProfileOrSocialChanges}
        >
          Cancel
        </Button>
      </div>
    </form>
  )
}
