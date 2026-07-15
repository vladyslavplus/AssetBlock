'use client'

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
    setSocialUrls,
    profileForm,
    profileFields,
    profileValues,
    savingProfile,
    hasProfileOrSocialChanges,
    saveBlockedBySocial,
    onSaveProfile,
    onCancelProfile,
  } = controller

  return (
    <form className="space-y-4" onSubmit={onSaveProfile} noValidate>
      <div className="space-y-2">
        <Label htmlFor="account-email">Email</Label>
        <Input
          id="account-email"
          value={profile?.email == null ? '' : String(profile.email)}
          readOnly
          disabled
          className="bg-muted/50"
        />
      </div>

      <div className="space-y-2">
        <Label htmlFor="account-username">Username</Label>
        <Input
          id="account-username"
          autoComplete="username"
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
                  onChange={(event) =>
                    setSocialUrls((previous) => ({
                      ...previous,
                      [platform.id]: event.target.value,
                    }))
                  }
                  disabled={socialPlatformsLoading || savingProfile}
                  className="bg-input border-border"
                />
              </div>
            ))}
        </div>
      </div>

      <div className="flex flex-wrap gap-2">
        <Button
          type="submit"
          disabled={savingProfile || !hasProfileOrSocialChanges || saveBlockedBySocial}
        >
          {savingProfile ? 'Saving…' : 'Save changes'}
        </Button>
        <Button
          type="button"
          variant="outline"
          onClick={onCancelProfile}
          disabled={!hasProfileOrSocialChanges}
        >
          Cancel
        </Button>
      </div>
    </form>
  )
}
