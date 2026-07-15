'use client'

import type { UseFormRegisterReturn } from 'react-hook-form'

import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import type { AccountSettingsController } from '@/lib/account/use-account-settings'

interface AccountPasswordFormProps {
  controller: AccountSettingsController
}

export function AccountPasswordForm({ controller }: AccountPasswordFormProps) {
  const { passwordForm, passwordFields, passwordValues, changingPassword, onChangePassword } =
    controller

  return (
    <form className="space-y-4" onSubmit={onChangePassword} noValidate>
      <PasswordField
        id="current-password"
        label="Current password"
        autoComplete="current-password"
        registration={passwordFields.currentPassword}
        value={passwordValues.currentPassword ?? ''}
        error={passwordForm.formState.errors.currentPassword?.message}
      />
      <PasswordField
        id="new-password"
        label="New password"
        autoComplete="new-password"
        registration={passwordFields.newPassword}
        value={passwordValues.newPassword ?? ''}
        error={passwordForm.formState.errors.newPassword?.message}
      />
      <PasswordField
        id="confirm-password"
        label="Confirm new password"
        autoComplete="new-password"
        registration={passwordFields.confirmPassword}
        value={passwordValues.confirmPassword ?? ''}
        error={passwordForm.formState.errors.confirmPassword?.message}
      />
      <Button type="submit" disabled={changingPassword}>
        {changingPassword ? 'Updating…' : 'Update password'}
      </Button>
    </form>
  )
}

interface PasswordFieldProps {
  id: string
  label: string
  autoComplete: string
  registration: UseFormRegisterReturn
  value: string
  error?: string
}

function PasswordField({
  id,
  label,
  autoComplete,
  registration,
  value,
  error,
}: PasswordFieldProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type="password" autoComplete={autoComplete} {...registration} value={value} />
      {error ? <p className="text-destructive text-sm">{error}</p> : null}
    </div>
  )
}
