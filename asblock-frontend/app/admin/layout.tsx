import { redirect } from "next/navigation";
import { SiteFooter } from "@/components/site-footer";
import { SiteHeader } from "@/components/site-header";
import { isAdminRole } from "@/lib/auth/roles";
import { getServerSessionUser } from "@/lib/server/session-user";

export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  const user = await getServerSessionUser();
  if (!user || !isAdminRole(user.role)) {
    redirect("/");
  }

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />
      <main className="flex-1 pt-20">
        <div className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="mb-8">
            <h1 className="text-2xl font-semibold text-foreground">Admin</h1>
            <p className="mt-1 text-sm text-muted-foreground">
              Manage categories, tags, and moderation tools. Signed in as{" "}
              <span className="font-mono text-foreground/90">{user.username}</span>.
            </p>
          </div>
          {children}
        </div>
      </main>
      <SiteFooter />
    </div>
  );
}
