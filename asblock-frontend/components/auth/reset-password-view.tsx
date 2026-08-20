'use client'

import { useMutation } from '@tanstack/react-query'
import { AlertCircle, CheckCircle2, Eye, EyeOff } from 'lucide-react'
import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { useState, useSyncExternalStore } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import { AuthRequestError, postPasswordResetConfirm } from '@/lib/auth/auth-api'
import { readAndClearEmailActionToken } from '@/lib/auth/email-action-token'
import { applyApiFieldErrorsToForm, parseApiErrorBody } from '@/lib/http/api-errors'
import { useAuth } from '@/components/auth/auth-context'

const schema = z
  .object({
    newPassword: z.string().min(8, 'Password must be at least 8 characters'),
    confirmPassword: z.string().min(1, 'Confirm your password'),
  })
  .refine((d) => d.newPassword === d.confirmPassword, {
    message: 'Passwords do not match',
    path: ['confirmPassword'],
  })

type FormValues = z.infer<typeof schema>

const emptySubscribe = () => () => {}

/** Survives Strict Mode remount; full page loads reset the module. */
let cachedResetToken: string | null | undefined

function getClientResetToken(): string | null {
  if (cachedResetToken === undefined) {
    cachedResetToken = readAndClearEmailActionToken()
  }
  return cachedResetToken
}

export function ResetPasswordView() {
  const router = useRouter()
  const { logout } = useAuth()
  // Match SSR on hydrate, then read #token= on the client without an effect.
  const isClient = useSyncExternalStore(
    emptySubscribe,
    () => true,
    () => false,
  )
  const token = isClient ? getClientResetToken() : null
  const tokenMissing = isClient && token === null
  const [submitError, setSubmitError] = useState('')
  const [success, setSuccess] = useState(false)
  const [showNew, setShowNew] = useState(false)
  const [showConfirm, setShowConfirm] = useState(false)

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { newPassword: '', confirmPassword: '' },
  })

  const mutation = useMutation({
    mutationFn: ({ newPassword }: FormValues) => {
      if (!token) return Promise.reject(new Error('Token is missing'))
      return postPasswordResetConfirm(token, newPassword)
    },
    onMutate: () => setSubmitError(''),
    onSuccess: () => {
      setSuccess(true)
      void logout().catch(() => undefined)
    },
    onError: (err: unknown) => {
      if (err instanceof AuthRequestError) {
        const parsed = parseApiErrorBody(err.body)
        if (parsed?.fieldErrors && Object.keys(parsed.fieldErrors).length > 0) {
          applyApiFieldErrorsToForm(setError, parsed.fieldErrors)
        }
        setSubmitError(err.message)
        return
      }
      setSubmitError('Network error. Try again.')
    },
  })

  const onSubmit = handleSubmit((values) => mutation.mutate(values))

  return (
    <Card className="border-border bg-card-elevated">
      <CardHeader className="pb-3 pt-5 px-5">
        <CardTitle className="text-xl">Reset password</CardTitle>
        <CardDescription className="text-xs text-muted-foreground">
          {success
            ? 'Your password has been reset.'
            : tokenMissing
              ? 'No reset token found.'
              : 'Enter a new password for your account.'}
        </CardDescription>
      </CardHeader>

      <Separator className="bg-border/50" />

      <CardContent className="pt-4 pb-3 px-5">
        {tokenMissing && !success && (
          <div className="flex flex-col gap-4">
            <Alert className="bg-destructive/10 border-destructive/30 py-2">
              <AlertCircle className="h-4 w-4 text-destructive shrink-0" />
              <AlertDescription className="text-destructive/90 text-xs">
                The reset link is missing or has expired. Please request a new one.
              </AlertDescription>
            </Alert>
            <Button
              type="button"
              className="w-full bg-primary text-primary-foreground hover:bg-[#6D28D9]"
              onClick={() => router.push('/forgot-password')}
            >
              Request new reset link
            </Button>
          </div>
        )}

        {success && (
          <div className="flex flex-col gap-4">
            <Alert className="bg-green-500/10 border-green-500/30 py-2">
              <CheckCircle2 className="h-4 w-4 text-green-500 shrink-0" />
              <AlertDescription className="text-green-500/90 text-xs">
                Password updated successfully. You can now sign in with your new password.
              </AlertDescription>
            </Alert>
            <Button
              type="button"
              className="w-full bg-primary text-primary-foreground hover:bg-[#6D28D9]"
              onClick={() => router.push('/login')}
            >
              Sign in
            </Button>
          </div>
        )}

        {!tokenMissing && !success && (
          <form onSubmit={onSubmit} className="flex flex-col gap-3">
            {submitError && (
              <Alert className="bg-destructive/10 border-destructive/30 py-2">
                <AlertCircle className="h-4 w-4 text-destructive shrink-0" />
                <AlertDescription className="text-destructive/90 text-xs">
                  {submitError}
                </AlertDescription>
              </Alert>
            )}

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="new-password" className="text-xs font-medium">
                New password
              </Label>
              <div className="relative">
                <Input
                  id="new-password"
                  type={showNew ? 'text' : 'password'}
                  autoComplete="new-password"
                  className="bg-secondary/30 border-border pr-10"
                  {...register('newPassword')}
                />
                <button
                  type="button"
                  className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground p-1"
                  onClick={() => setShowNew((v) => !v)}
                  aria-label={showNew ? 'Hide password' : 'Show password'}
                >
                  {showNew ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                </button>
              </div>
              {errors.newPassword && (
                <p className="text-xs text-destructive">{errors.newPassword.message}</p>
              )}
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="confirm-password" className="text-xs font-medium">
                Confirm password
              </Label>
              <div className="relative">
                <Input
                  id="confirm-password"
                  type={showConfirm ? 'text' : 'password'}
                  autoComplete="new-password"
                  className="bg-secondary/30 border-border pr-10"
                  {...register('confirmPassword')}
                />
                <button
                  type="button"
                  className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground p-1"
                  onClick={() => setShowConfirm((v) => !v)}
                  aria-label={showConfirm ? 'Hide password' : 'Show password'}
                >
                  {showConfirm ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                </button>
              </div>
              {errors.confirmPassword && (
                <p className="text-xs text-destructive">{errors.confirmPassword.message}</p>
              )}
            </div>

            <Button
              type="submit"
              className="w-full mt-1 bg-primary text-primary-foreground hover:bg-[#6D28D9]"
              disabled={mutation.isPending}
            >
              {mutation.isPending ? 'Resetting…' : 'Reset password'}
            </Button>
          </form>
        )}

        {!success && (
          <div className="mt-3 text-xs text-center">
            <span className="text-muted-foreground">Remembered your password? </span>
            <Link
              href="/login"
              className="text-foreground font-medium hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
            >
              Sign in
            </Link>
          </div>
        )}
      </CardContent>

      <div className="px-5 py-3 border-t border-border/30 text-center">
        <Link
          href="/"
          className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
        >
          ← Back to AssetBlock
        </Link>
      </div>
    </Card>
  )
}
