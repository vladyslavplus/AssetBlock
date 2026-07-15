import { redirect } from 'next/navigation'
import { SiteMain } from '@/components/layout/site-main'
import { SitePageContainer } from '@/components/layout/site-page-container'
import { SiteFooter } from '@/components/site-footer'
import { SiteHeader } from '@/components/site-header'
import { isAdminRole } from '@/lib/auth/roles'
import { getServerSessionUser } from '@/lib/server/session-user'

export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  const user = await getServerSessionUser()
  if (!user || !isAdminRole(user.role)) {
    redirect('/')
  }

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <SiteMain>
        <SitePageContainer variant="admin">
          <div className="mb-8">
            <h1 className="text-2xl font-semibold text-foreground">Admin</h1>
            <p className="mt-1 text-sm text-muted-foreground">
              Manage categories, tags, and moderation tools. Signed in as{' '}
              <span className="font-mono text-foreground/90">{user.username}</span>.
            </p>
          </div>
          {children}
        </SitePageContainer>
      </SiteMain>
      <SiteFooter />
    </div>
  )
}
