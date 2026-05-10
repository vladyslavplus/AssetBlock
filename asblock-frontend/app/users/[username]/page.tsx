import type { Metadata } from "next";
import Image from "next/image";
import { notFound } from "next/navigation";
import { Calendar, ExternalLink } from "lucide-react";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { Button } from "@/components/ui/button";
import { AuthorCatalogSection } from "@/components/users/author-catalog-section";
import { formatLongMonthYear } from "@/lib/format-date";
import { fetchAuthorAssetsPage, fetchPublicProfileByUsername } from "@/lib/server/user-profile-server";

interface PageProps {
  params: Promise<{ username: string }>;
  searchParams: Promise<{ page?: string }>;
}

export async function generateMetadata({ params }: PageProps): Promise<Metadata> {
  const { username } = await params;
  const profile = await fetchPublicProfileByUsername(username);
  if (!profile) {
    return { title: "Profile not found · AssetBlock" };
  }
  const bio = profile.bio?.trim();
  return {
    title: `@${profile.username} · AssetBlock`,
    description:
      bio && bio.length > 0
        ? bio.slice(0, 160)
        : `Digital assets and listings by @${profile.username} on AssetBlock.`,
  };
}

export default async function PublicUserProfilePage({ params, searchParams }: PageProps) {
  const { username } = await params;
  const sp = await searchParams;
  const pageRaw = sp.page;
  const page = Math.max(1, Number.parseInt(pageRaw ?? "1", 10) || 1);

  const profile = await fetchPublicProfileByUsername(username);
  if (!profile) {
    notFound();
  }

  const catalog = await fetchAuthorAssetsPage(profile.id, page);

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="rounded-xl border border-border bg-card-elevated p-6 sm:p-8 mb-10">
            <div className="flex flex-col gap-6 sm:flex-row sm:items-start">
              <div className="shrink-0">
                {profile.avatarUrl?.trim() ? (
                  <div className="relative size-20 sm:size-24 overflow-hidden rounded-xl border border-border bg-secondary">
                    <Image
                      src={profile.avatarUrl.trim()}
                      alt={`@${profile.username} avatar`}
                      width={96}
                      height={96}
                      className="size-full object-cover"
                      unoptimized
                    />
                  </div>
                ) : (
                  <div
                    className="size-20 sm:size-24 rounded-xl bg-primary/15 border border-primary/35 flex items-center justify-center"
                    aria-hidden
                  >
                    <span className="text-3xl font-bold text-primary/80">
                      {profile.username.charAt(0).toUpperCase()}
                    </span>
                  </div>
                )}
              </div>

              <div className="min-w-0 flex-1 space-y-3">
                <div>
                  <h1 className="text-2xl sm:text-3xl font-bold text-foreground tracking-tight">
                    @{profile.username}
                  </h1>
                  <p className="text-xs text-muted-foreground mt-1 flex items-center gap-1.5">
                    <Calendar className="size-3.5 shrink-0" aria-hidden />
                    Member since {formatLongMonthYear(profile.createdAt)}
                  </p>
                </div>

                {profile.bio?.trim() ? (
                  <p className="text-sm text-muted-foreground leading-relaxed max-w-2xl whitespace-pre-wrap break-words [overflow-wrap:anywhere]">
                    {profile.bio.trim()}
                  </p>
                ) : (
                  <p className="text-sm text-muted-foreground/80 italic">No bio yet.</p>
                )}

                {profile.socialLinks.length > 0 && (
                  <ul className="flex flex-wrap gap-2 pt-1">
                    {profile.socialLinks.map((link) => (
                      <li key={link.id}>
                        <Button
                          asChild
                          variant="outline"
                          size="sm"
                          className="h-8 text-xs font-medium border-border bg-transparent hover:bg-secondary/50"
                        >
                          <a
                            href={link.url}
                            target="_blank"
                            rel="noopener noreferrer me"
                            className="inline-flex items-center gap-1.5"
                          >
                            <ExternalLink className="size-3.5 shrink-0 opacity-70" aria-hidden />
                            {link.platformName}
                          </a>
                        </Button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </div>
          </div>

          <AuthorCatalogSection authorId={profile.id} username={profile.username} initialCatalog={catalog} />
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
