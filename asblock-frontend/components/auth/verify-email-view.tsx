'use client'

import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertCircle, CheckCircle2, Loader2 } from 'lucide-react'
import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { useEffect, useEffectEvent, useState } from 'react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { AuthRequestError, postEmailVerificationConfirm } from '@/lib/auth/auth-api'
import { readAndClearEmailActionToken } from '@/lib/auth/email-action-token'
import { accountKeys } from '@/lib/account/account-query'

export function VerifyEmailView() {
  const router = useRouter()
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState(false)

  const mutation = useMutation({
    mutationFn: postEmailVerificationConfirm,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: accountKeys.me() })
      setSuccess(true)
    },
    onError: (err: unknown) => {
      if (err instanceof AuthRequestError) {
        setError(err.message)
        return
      }
      setError('Network error. Try again later.')
    },
  })

  const confirmFromHash = useEffectEvent(() => {
    const token = readAndClearEmailActionToken()
    if (!token) {
      setError('No verification token found in the link. Please use the link from your email.')
      return
    }
    mutation.mutate(token)
  })

  useEffect(() => {
    confirmFromHash()
  }, [])

  return (
    <Card className="border-border bg-card-elevated">
      <CardHeader className="pb-3 pt-5 px-5">
        <CardTitle className="text-xl">Email verification</CardTitle>
        <CardDescription className="text-xs text-muted-foreground">
          Confirming your email address&hellip;
        </CardDescription>
      </CardHeader>

      <Separator className="bg-border/50" />

      <CardContent className="pt-5 pb-4 px-5 flex flex-col gap-4">
        {mutation.isPending && (
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin shrink-0" />
            Verifying your email address…
          </div>
        )}

        {success && (
          <Alert className="bg-green-500/10 border-green-500/30 py-2">
            <CheckCircle2 className="h-4 w-4 text-green-500 shrink-0" />
            <AlertDescription className="text-green-500/90 text-xs">
              Your email address has been verified successfully.
            </AlertDescription>
          </Alert>
        )}

        {error && (
          <Alert className="bg-destructive/10 border-destructive/30 py-2">
            <AlertCircle className="h-4 w-4 text-destructive shrink-0" />
            <AlertDescription className="text-destructive/90 text-xs">{error}</AlertDescription>
          </Alert>
        )}

        {(success || error) && (
          <div className="flex flex-wrap gap-2">
            {success && (
              <Button
                type="button"
                className="bg-primary text-primary-foreground hover:bg-[#6D28D9]"
                onClick={() => router.push('/account')}
              >
                Go to account
              </Button>
            )}
            <Button type="button" variant="outline" onClick={() => router.push('/login')}>
              Sign in
            </Button>
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
