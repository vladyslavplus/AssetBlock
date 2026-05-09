import { notFound } from "next/navigation";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { Button } from "@/components/ui/button";
import Link from "next/link";
import { ExternalLink, Calendar } from "lucide-react";

interface MOCK_PUBLIC_USER {
  id: string;
  username: string;
  bio?: string;
  joinedAt?: string;
}

const MOCK_PUBLIC_USERS: Record<string, MOCK_PUBLIC_USER> = {
  "usr-001": {
    id: "usr-001",
    username: "devcraft",
    bio: "Full-stack developer creating premium dashboard templates and UI components. Passionate about TypeScript and clean code.",
    joinedAt: "2024-06-15T00:00:00Z",
  },
  "usr-002": {
    id: "usr-002",
    username: "saasforge",
    bio: "SaaS specialist building production-ready starter kits and boilerplates. 5+ years building and shipping products.",
    joinedAt: "2024-03-22T00:00:00Z",
  },
  "usr-003": {
    id: "usr-003",
    username: "uicraft",
    bio: "Designer and developer crafting beautiful, accessible UI components and design systems.",
    joinedAt: "2024-08-10T00:00:00Z",
  },
};

export const metadata = {
  title: "Author profile - AssetBlock",
  description: "View author profile and their digital assets.",
};

interface UserProfilePageProps {
  params: { id: string };
}

export default function UserProfilePage({ params }: UserProfilePageProps) {
  const user = MOCK_PUBLIC_USERS[params.id];

  if (!user) {
    notFound();
  }

  const dateFormatter = new Intl.DateTimeFormat("en-US", {
    year: "numeric",
    month: "long",
  });

  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {/* Profile header */}
          <div className="mb-8">
            <div className="flex items-start gap-4">
              {/* Avatar placeholder */}
              <div className="w-16 h-16 rounded-xl bg-primary/20 border border-primary/40 flex items-center justify-center flex-shrink-0">
                <span className="text-2xl font-bold text-primary/60">
                  {user.username.charAt(0).toUpperCase()}
                </span>
              </div>

              {/* Profile info */}
              <div className="flex-1">
                <h1 className="text-2xl font-bold text-foreground">
                  @{user.username}
                </h1>
                {user.bio && (
                  <p className="text-sm text-muted-foreground mt-1">
                    {user.bio}
                  </p>
                )}
                {user.joinedAt && (
                  <p className="text-xs text-muted-foreground mt-2 flex items-center gap-1.5">
                    <Calendar className="w-3.5 h-3.5" />
                    Member since{" "}
                    {dateFormatter.format(new Date(user.joinedAt))}
                  </p>
                )}
              </div>
            </div>
          </div>

          {/* Divider */}
          <div className="border-t border-border/30 my-8" />

          {/* Assets section */}
          <div>
            <h2 className="text-xl font-semibold text-foreground mb-4">
              Assets by this author
            </h2>

            <div className="bg-card-elevated border border-border rounded-xl p-6 space-y-3">
              <p className="text-sm text-muted-foreground">
                Asset list and filtering by author will be available after backend
                wiring. For now, you can browse all assets and filter by author
                manually.
              </p>
              <Button
                asChild
                variant="outline"
                className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth font-medium"
              >
                <Link href="/assets">
                  <ExternalLink className="w-4 h-4 mr-2" />
                  Browse all assets
                </Link>
              </Button>
            </div>
          </div>
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
