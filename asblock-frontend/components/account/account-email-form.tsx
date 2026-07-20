'use client'

import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import type { AccountSettingsController } from '@/lib/account/use-account-settings'

interface AccountEmailFormProps {
  controller: AccountSettingsController
}

export function AccountEmailForm({ controller }: AccountEmailFormProps) {
  const {
    emailChangeForm,
    emailChangeFields,
    emailChangeValues,
    requestingEmailChange,
    onRequestEmailChange,
  } = controller

  return (
    <form className="space-y-4" onSubmit={onRequestEmailChange} noValidate>
      <div className="space-y-2">
        <Label htmlFor="new-email">New email address</Label>
        <Input
          id="new-email"
          type="email"
          autoComplete="email"
          placeholder="new@example.com"
          {...emailChangeFields.newEmail}
          value={emailChangeValues.newEmail ?? ''}
        />
        {emailChangeForm.formState.errors.newEmail ? (
          <p className="text-destructive text-sm">
            {emailChangeForm.formState.errors.newEmail.message}
          </p>
        ) : null}
      </div>

      <div className="space-y-2">
        <Label htmlFor="email-change-password">Current password</Label>
        <Input
          id="email-change-password"
          type="password"
          autoComplete="current-password"
          {...emailChangeFields.currentPassword}
          value={emailChangeValues.currentPassword ?? ''}
        />
        {emailChangeForm.formState.errors.currentPassword ? (
          <p className="text-destructive text-sm">
            {emailChangeForm.formState.errors.currentPassword.message}
          </p>
        ) : null}
      </div>

      <p className="text-xs text-muted-foreground">
        A confirmation link will be sent to your new email address. Your email changes after you
        click the link.
      </p>

      <Button type="submit" disabled={requestingEmailChange}>
        {requestingEmailChange ? 'Sending…' : 'Request email change'}
      </Button>
    </form>
  )
}
