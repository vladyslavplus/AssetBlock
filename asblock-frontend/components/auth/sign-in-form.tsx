'use client'

import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Eye, EyeOff, AlertCircle } from 'lucide-react'
import Link from 'next/link'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { AuthRequestError, postAuthLogin } from '@/lib/auth/auth-api'
import { loginFormSchema, type LoginFormValues } from '@/lib/auth/schemas'
import { syncQueryCacheAfterAuth } from '@/lib/query/query-sync-after-auth'

const MESSAGE_LABELS: Record<string, string> = {
  'password-changed': 'Password changed. Please sign in again.',
}

interface SignInFormProps {
  formError?: string
}

export function SignInForm({ formError }: SignInFormProps) {
  const router = useRouter()
  const searchParams = useSearchParams()
  const queryClient = useQueryClient()
  const [showPassword, setShowPassword] = useState(false)
  const [submitError, setSubmitError] = useState<string>('')

  const returnUrl = searchParams.get('returnUrl')?.trim() || '/assets'

  const messageToastShownRef = useRef(false)

  useEffect(() => {
    if (messageToastShownRef.current) return
    const msg = searchParams.get('message')
    if (msg && MESSAGE_LABELS[msg]) {
      messageToastShownRef.current = true
      toast.success(MESSAGE_LABELS[msg])
    }
  }, [searchParams])

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginFormValues>({
    resolver: zodResolver(loginFormSchema),
    defaultValues: { email: '', password: '' },
  })

  const loginMutation = useMutation({
    mutationFn: postAuthLogin,
    onMutate: () => setSubmitError(''),
    onSuccess: async () => {
      await syncQueryCacheAfterAuth(queryClient)
      const next = returnUrl.startsWith('/') ? returnUrl : '/assets'
      router.push(next)
      router.refresh()
    },
    onError: (err: unknown) => {
      if (err instanceof AuthRequestError) {
        setSubmitError(err.message)
        return
      }
      setSubmitError('Network error. Try again.')
    },
  })

  const onSubmit = handleSubmit((values) => loginMutation.mutate(values))

  return (
    <form onSubmit={onSubmit} className="flex flex-col gap-3">
      {(formError || submitError) && (
        <Alert className="bg-destructive/10 border-destructive/30 py-2">
          <AlertCircle className="h-4 w-4 text-destructive shrink-0" />
          <AlertDescription className="text-destructive/90 text-xs">
            {formError || submitError}
          </AlertDescription>
        </Alert>
      )}

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="email" className="text-xs font-medium">
          Email
        </Label>
        <Input
          id="email"
          type="email"
          autoComplete="email"
          className="bg-secondary/30 border-border"
          {...register('email')}
        />
        {errors.email && <p className="text-xs text-destructive">{errors.email.message}</p>}
      </div>

      <div className="flex flex-col gap-1.5">
        <div className="flex items-center justify-between">
          <Label htmlFor="password" className="text-xs font-medium">
            Password
          </Label>
          <Link
            href="/forgot-password"
            className="text-xs text-muted-foreground hover:text-foreground transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
          >
            Forgot password?
          </Link>
        </div>
        <div className="relative">
          <Input
            id="password"
            type={showPassword ? 'text' : 'password'}
            autoComplete="current-password"
            className="bg-secondary/30 border-border pr-10"
            {...register('password')}
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground p-1"
            onClick={() => setShowPassword((v) => !v)}
            aria-label={showPassword ? 'Hide password' : 'Show password'}
          >
            {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
          </button>
        </div>
        {errors.password && <p className="text-xs text-destructive">{errors.password.message}</p>}
      </div>

      <Button
        type="submit"
        className="w-full mt-1 bg-primary text-primary-foreground hover:bg-[#6D28D9]"
        disabled={loginMutation.isPending}
      >
        {loginMutation.isPending ? 'Signing in…' : 'Sign in'}
      </Button>
    </form>
  )
}
