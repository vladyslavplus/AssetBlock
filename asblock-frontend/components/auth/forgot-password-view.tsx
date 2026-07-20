'use client'

import { useMutation } from '@tanstack/react-query'
import { AlertCircle, CheckCircle2 } from 'lucide-react'
import Link from 'next/link'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import { AuthRequestError, postPasswordResetRequest } from '@/lib/auth/auth-api'

const schema = z.object({
  email: z.string().min(1, 'Email is required').email('Enter a valid email address'),
})
type FormValues = z.infer<typeof schema>

export function ForgotPasswordView() {
  const [submitError, setSubmitError] = useState('')
  const [sent, setSent] = useState(false)

  const {
    register,
    handleSubmit,
    formState: { errors },
    getValues,
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { email: '' },
  })

  const mutation = useMutation({
    mutationFn: (values: FormValues) => postPasswordResetRequest(values.email),
    onMutate: () => setSubmitError(''),
    onSuccess: () => setSent(true),
    onError: (err: unknown) => {
      if (err instanceof AuthRequestError) {
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
        <CardTitle className="text-xl">Forgot password</CardTitle>
        <CardDescription className="text-xs text-muted-foreground">
          Enter your email address and we&apos;ll send a reset link.
        </CardDescription>
      </CardHeader>

      <Separator className="bg-border/50" />

      <CardContent className="pt-4 pb-3 px-5">
        {sent ? (
          <div className="flex flex-col gap-4">
            <Alert className="bg-green-500/10 border-green-500/30 py-2">
              <CheckCircle2 className="h-4 w-4 text-green-500 shrink-0" />
              <AlertDescription className="text-green-500/90 text-xs">
                If an account exists for <strong>{getValues('email')}</strong>, a reset link has
                been sent. Check your inbox.
              </AlertDescription>
            </Alert>
            <p className="text-xs text-muted-foreground text-center">
              Didn&apos;t receive an email?{' '}
              <button
                type="button"
                className="text-foreground font-medium hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
                onClick={() => {
                  setSent(false)
                  setSubmitError('')
                }}
              >
                Try again
              </button>
            </p>
          </div>
        ) : (
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
              <Label htmlFor="forgot-email" className="text-xs font-medium">
                Email address
              </Label>
              <Input
                id="forgot-email"
                type="email"
                autoComplete="email"
                className="bg-secondary/30 border-border"
                {...register('email')}
              />
              {errors.email && <p className="text-xs text-destructive">{errors.email.message}</p>}
            </div>

            <Button
              type="submit"
              className="w-full mt-1 bg-primary text-primary-foreground hover:bg-[#6D28D9]"
              disabled={mutation.isPending}
            >
              {mutation.isPending ? 'Sending…' : 'Send reset link'}
            </Button>
          </form>
        )}

        <div className="mt-3 text-xs text-center">
          <span className="text-muted-foreground">Remembered your password? </span>
          <Link
            href="/login"
            className="text-foreground font-medium hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
          >
            Sign in
          </Link>
        </div>
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
