import type { Metadata } from "next";
import { SiteHeader } from "@/components/site-header";
import { SiteFooter } from "@/components/site-footer";
import { AccountSettingsForm } from "@/components/account/account-settings-form";

export const metadata: Metadata = {
  title: "Account - AssetBlock",
  description: "Manage your account settings and profile.",
};

export default function AccountPage() {
  return (
    <div className="min-h-screen flex flex-col bg-background text-foreground">
      <SiteHeader />

      <main className="flex-1 pt-20 pb-16">
        <div className="max-w-2xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="mb-8">
            <h1 className="text-3xl font-bold text-foreground mb-2">Account</h1>
            <p className="text-sm text-muted-foreground">
              Manage your profile and settings
            </p>
          </div>

          <AccountSettingsForm />
        </div>
      </main>

      <SiteFooter />
    </div>
  );
}
