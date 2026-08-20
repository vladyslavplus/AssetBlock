import type { Metadata } from 'next'
import { ResetPasswordView } from '@/components/auth/reset-password-view'

export const metadata: Metadata = {
  title: 'Reset password - AssetBlock',
  description: 'Set a new password for your AssetBlock account.',
  referrer: 'no-referrer',
}

export default function ResetPasswordPage() {
  return (
    <main className="min-h-screen bg-background text-foreground flex items-center justify-center px-4 py-12">
      <div
        className="fixed inset-0 opacity-[0.035] pointer-events-none"
        style={{
          backgroundImage:
            'linear-gradient(to right, #9A96B0 1px, transparent 1px), linear-gradient(to bottom, #9A96B0 1px, transparent 1px)',
          backgroundSize: '40px 40px',
        }}
        aria-hidden="true"
      />
      <div
        className="fixed inset-0 opacity-10 pointer-events-none"
        style={{
          background: 'radial-gradient(ellipse at center, #7C3AED 0%, transparent 70%)',
        }}
        aria-hidden="true"
      />
      <div className="relative z-10 w-full max-w-md">
        <ResetPasswordView />
      </div>
    </main>
  )
}
