"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { zodResolver } from "@hookform/resolvers/zod";
import { ArrowLeft } from "lucide-react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import type { AccountProfile, UpdateUserProfileResponseDto } from "@/lib/account-types";
import {
  accountProfileFormSchema,
  type AccountProfileFormValues,
  changePasswordFormSchema,
  type ChangePasswordFormValues,
} from "@/lib/account-schemas";

function readApiErrorMessage(payload: unknown, fallback: string): string {
  if (!payload || typeof payload !== "object") return fallback;
  const o = payload as Record<string, unknown>;
  if (typeof o.error === "string" && o.error.length > 0) return o.error;
  const errors = o.errors;
  if (Array.isArray(errors) && errors.length > 0) {
    const first = errors[0];
    if (first && typeof first === "object" && "message" in first) {
      const m = (first as { message: unknown }).message;
      if (typeof m === "string" && m.length > 0) return m;
    }
  }
  return fallback;
}

type AccountSection = "profile" | "password";

const PASSWORD_FORM_EMPTY: ChangePasswordFormValues = {
  currentPassword: "",
  newPassword: "",
  confirmPassword: "",
};

export function AccountSettingsForm() {
  const router = useRouter();
  const [loadState, setLoadState] = useState<"idle" | "loading" | "error">("idle");
  const [loadError, setLoadError] = useState<string | null>(null);
  const [profile, setProfile] = useState<AccountProfile | null>(null);
  const [section, setSection] = useState<AccountSection>("profile");
  const [savingProfile, setSavingProfile] = useState(false);
  const [changingPassword, setChangingPassword] = useState(false);
  const lastSavedRef = useRef<AccountProfileFormValues | null>(null);

  const profileForm = useForm<AccountProfileFormValues>({
    resolver: zodResolver(accountProfileFormSchema),
    defaultValues: {
      username: "",
      bio: "",
      avatarUrl: "",
      isPublicProfile: true,
    },
  });

  const passwordForm = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordFormSchema),
    defaultValues: {
      currentPassword: "",
      newPassword: "",
      confirmPassword: "",
    },
  });

  const fetchProfile = useCallback(async () => {
    setLoadState("loading");
    setLoadError(null);
    try {
      const res = await fetch("/api/account/me", { credentials: "include" });
      if (res.status === 401) {
        router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
        return;
      }
      const json: unknown = await res.json().catch(() => null);
      if (!res.ok) {
        setLoadState("error");
        setLoadError(readApiErrorMessage(json, "Could not load profile."));
        return;
      }
      const data = json as AccountProfile;
      setProfile(data);
      const values: AccountProfileFormValues = {
        username: data.username,
        bio: data.bio ?? "",
        avatarUrl: data.avatarUrl ?? "",
        isPublicProfile: data.isPublicProfile,
      };
      lastSavedRef.current = values;
      profileForm.reset(values);
      setLoadState("idle");
    } catch {
      setLoadState("error");
      setLoadError("Network error. Try again.");
    }
  }, [profileForm, router]);

  useEffect(() => {
    void fetchProfile();
  }, [fetchProfile]);

  const onSaveProfile = profileForm.handleSubmit(async (values) => {
    setSavingProfile(true);
    try {
      const res = await fetch("/api/account/me", {
        method: "PATCH",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          username: values.username.trim(),
          bio: values.bio.trim() || null,
          avatarUrl: values.avatarUrl.trim() || null,
          isPublicProfile: values.isPublicProfile,
        }),
      });
      const json: unknown = await res.json().catch(() => null);
      if (res.status === 401) {
        router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
        return;
      }
      if (!res.ok) {
        toast.error(readApiErrorMessage(json, "Could not save profile."));
        return;
      }
      const data = json as UpdateUserProfileResponseDto;
      setProfile((prev) =>
        prev
          ? {
              ...prev,
              username: data.username,
              avatarUrl: data.avatarUrl,
              bio: data.bio,
              isPublicProfile: data.isPublicProfile,
            }
          : prev,
      );
      const next: AccountProfileFormValues = {
        username: data.username,
        bio: data.bio ?? "",
        avatarUrl: data.avatarUrl ?? "",
        isPublicProfile: data.isPublicProfile,
      };
      lastSavedRef.current = next;
      profileForm.reset(next);
      toast.success("Profile saved.");
      router.refresh();
    } catch {
      toast.error("Network error. Try again.");
    } finally {
      setSavingProfile(false);
    }
  });

  const onCancelProfile = () => {
    const snap = lastSavedRef.current;
    if (snap) profileForm.reset(snap);
  };

  const openPasswordSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY);
    setSection("password");
  };

  const backToProfileSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY);
    setSection("profile");
  };

  const onChangePassword = passwordForm.handleSubmit(async (values) => {
    setChangingPassword(true);
    try {
      const res = await fetch("/api/account/password", {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          currentPassword: values.currentPassword,
          newPassword: values.newPassword,
        }),
      });
      const json: unknown = await res.json().catch(() => null);
      if (res.status === 401) {
        router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
        return;
      }
      if (!res.ok) {
        toast.error(readApiErrorMessage(json, "Could not change password."));
        return;
      }
      passwordForm.reset(PASSWORD_FORM_EMPTY);
      toast.success("Password updated.");
      setSection("profile");
    } catch {
      toast.error("Network error. Try again.");
    } finally {
      setChangingPassword(false);
    }
  });

  if (loadState === "loading" && !profile) {
    return (
      <Card className="border-border bg-card-elevated">
        <CardHeader>
          <CardTitle className="text-xl">Account</CardTitle>
          <CardDescription>Loading your profile…</CardDescription>
        </CardHeader>
        <CardContent>
          <p className="text-muted-foreground text-sm">Please wait.</p>
        </CardContent>
      </Card>
    );
  }

  if (loadState === "error" && !profile) {
    return (
      <Card className="border-border bg-card-elevated">
        <CardHeader>
          <CardTitle className="text-xl">Account</CardTitle>
          <CardDescription>We could not load your profile.</CardDescription>
        </CardHeader>
        <CardContent>
          <p className="text-destructive text-sm">{loadError}</p>
          <Button type="button" variant="outline" className="mt-4" onClick={() => void fetchProfile()}>
            Retry
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card className="border-border bg-card-elevated">
      {section === "profile" ? (
        <CardHeader className="space-y-4">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div className="space-y-1.5">
              <CardTitle className="text-xl">Profile</CardTitle>
              <CardDescription>
                Update how you appear on AssetBlock. Email is read-only; contact support to change it.
              </CardDescription>
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="shrink-0 self-start"
              onClick={openPasswordSection}
            >
              Change password
            </Button>
          </div>
        </CardHeader>
      ) : (
        <CardHeader className="space-y-4 pb-2">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="-ml-2 h-8 w-fit gap-1.5 px-2 text-muted-foreground hover:text-foreground"
            onClick={backToProfileSection}
          >
            <ArrowLeft className="size-4 shrink-0" aria-hidden />
            Back to profile
          </Button>
          <div className="space-y-1.5">
            <CardTitle className="text-xl">Password</CardTitle>
            <CardDescription>Use a strong password you do not reuse elsewhere.</CardDescription>
          </div>
        </CardHeader>
      )}

      <CardContent>
        {section === "profile" ? (
          <form className="space-y-4" onSubmit={onSaveProfile} noValidate>
            <div className="space-y-2">
              <Label htmlFor="account-email">Email</Label>
              <Input id="account-email" value={profile?.email ?? ""} readOnly disabled className="bg-muted/50" />
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-username">Username</Label>
              <Input id="account-username" autoComplete="username" {...profileForm.register("username")} />
              {profileForm.formState.errors.username && (
                <p className="text-destructive text-sm">{profileForm.formState.errors.username.message}</p>
              )}
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-bio">Bio</Label>
              <Textarea id="account-bio" rows={4} {...profileForm.register("bio")} />
              {profileForm.formState.errors.bio && (
                <p className="text-destructive text-sm">{profileForm.formState.errors.bio.message}</p>
              )}
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-avatar">Avatar URL</Label>
              <Input id="account-avatar" type="url" placeholder="https://…" {...profileForm.register("avatarUrl")} />
              {profileForm.formState.errors.avatarUrl && (
                <p className="text-destructive text-sm">{profileForm.formState.errors.avatarUrl.message}</p>
              )}
            </div>

            <div className="flex items-center justify-between gap-4 rounded-lg border p-3">
              <div>
                <p className="text-sm font-medium">Public profile</p>
                <p className="text-muted-foreground text-xs">Allow others to view your profile page.</p>
              </div>
              <Switch
                checked={profileForm.watch("isPublicProfile")}
                onCheckedChange={(v) => profileForm.setValue("isPublicProfile", v, { shouldDirty: true })}
              />
            </div>

            <div className="flex flex-wrap gap-2">
              <Button type="submit" disabled={savingProfile || !profileForm.formState.isDirty}>
                {savingProfile ? "Saving…" : "Save changes"}
              </Button>
              <Button type="button" variant="outline" onClick={onCancelProfile} disabled={!profileForm.formState.isDirty}>
                Cancel
              </Button>
            </div>
          </form>
        ) : (
          <form className="space-y-4" onSubmit={onChangePassword} noValidate>
            <div className="space-y-2">
              <Label htmlFor="current-password">Current password</Label>
              <Input
                id="current-password"
                type="password"
                autoComplete="current-password"
                {...passwordForm.register("currentPassword")}
              />
              {passwordForm.formState.errors.currentPassword && (
                <p className="text-destructive text-sm">{passwordForm.formState.errors.currentPassword.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="new-password">New password</Label>
              <Input
                id="new-password"
                type="password"
                autoComplete="new-password"
                {...passwordForm.register("newPassword")}
              />
              {passwordForm.formState.errors.newPassword && (
                <p className="text-destructive text-sm">{passwordForm.formState.errors.newPassword.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="confirm-password">Confirm new password</Label>
              <Input
                id="confirm-password"
                type="password"
                autoComplete="new-password"
                {...passwordForm.register("confirmPassword")}
              />
              {passwordForm.formState.errors.confirmPassword && (
                <p className="text-destructive text-sm">{passwordForm.formState.errors.confirmPassword.message}</p>
              )}
            </div>
            <Button type="submit" disabled={changingPassword}>
              {changingPassword ? "Updating…" : "Update password"}
            </Button>
          </form>
        )}
      </CardContent>
    </Card>
  );
}
