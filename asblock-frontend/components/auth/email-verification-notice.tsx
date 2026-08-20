'use client'

import Link from 'next/link'
import { MailWarning } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { SessionUser } from '@/lib/auth/auth-types'

export function isEmailVerified(user: SessionUser | null | undefined): boolean {
  return Boolean(user?.emailVerifiedAt)
}

interface EmailVerificationNoticeProps {
  className?: string
  /** Compact strip for global chrome (e.g. site header). */
  compact?: boolean
}

/** Persistent UX notice for authenticated unverified users. Not a security boundary. */
export function EmailVerificationNotice({
  className,
  compact = false,
}: EmailVerificationNoticeProps) {
  if (compact) {
    return (
      <div
        role="status"
        className={cn('border-b border-amber-500/30 bg-amber-500/10 text-amber-100/95', className)}
      >
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-3 px-4 py-2 text-xs sm:text-sm">
          <p className="flex min-w-0 items-center gap-2">
            <MailWarning className="size-3.5 shrink-0" aria-hidden />
            <span className="truncate">
              Verify your email to sell, buy, review, and update your public profile.
            </span>
          </p>
          <Button
            asChild
            size="sm"
            variant="outline"
            className="h-7 shrink-0 border-amber-500/40 bg-transparent text-amber-50 hover:bg-amber-500/20 hover:text-amber-50"
          >
            <Link href="/account">Account</Link>
          </Button>
        </div>
      </div>
    )
  }

  return (
    <div
      role="status"
      className={cn(
        'flex flex-col gap-3 rounded-lg border border-amber-500/35 bg-amber-500/10 px-4 py-3 sm:flex-row sm:items-center sm:justify-between',
        className,
      )}
    >
      <div className="flex min-w-0 items-start gap-2 text-sm text-amber-100/95">
        <MailWarning className="mt-0.5 size-4 shrink-0" aria-hidden />
        <p>
          Email verification is required for marketplace actions (upload, purchase, reviews, and
          public profile edits). You can still manage security settings and access purchases you
          already own. Resend the link from your account page.
        </p>
      </div>
      <Button
        asChild
        size="sm"
        variant="outline"
        className="shrink-0 border-amber-500/40 bg-transparent text-amber-50 hover:bg-amber-500/20"
      >
        <Link href="/account">Go to account</Link>
      </Button>
    </div>
  )
}
