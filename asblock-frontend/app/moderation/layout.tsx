import { redirect } from 'next/navigation'
import { EmailVerificationNotice } from '@/components/auth/email-verification-notice'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteFooter } from '@/components/site-footer'
import { SiteHeader } from '@/components/site-header'
import { isModeratorRole } from '@/lib/auth/roles'
import { getServerSessionUser } from '@/lib/server/session-user'

export default async function ModerationLayout({ children }: { children: React.ReactNode }) {
  const user = await getServerSessionUser()
  if (!user || !isModeratorRole(user.role)) {
    redirect('/')
  }

  const verified = Boolean(user.emailVerifiedAt)

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="document" padding="document">
          <div className="mb-8">
            <h1 className="text-2xl font-semibold text-foreground">Moderation</h1>
            <p className="mt-1 text-sm text-muted-foreground">
              Signed in as <span className="font-mono text-foreground/90">{user.username}</span>.
            </p>
          </div>
          {verified ? (
            children
          ) : (
            <div className="space-y-4">
              <EmailVerificationNotice />
              <p className="text-sm text-muted-foreground">
                Moderation tools stay locked until your email is verified. The API enforces this
                even if the UI is bypassed.
              </p>
            </div>
          )}
        </SitePageContainer>
      </SiteMain>
      <SiteFooter />
    </div>
  )
}
