"use client";

/* eslint-disable react-hooks/refs -- react-hook-form: field refs from register() and handleSubmit() are valid usage */

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { zodResolver } from "@hookform/resolvers/zod";
import { ArrowLeft } from "lucide-react";
import { useForm, useWatch } from "react-hook-form";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import type { AccountProfile } from "@/lib/account/account-types";
import {
  accountProfileFormSchema,
  type AccountProfileFormValues,
  changePasswordFormSchema,
  type ChangePasswordFormValues,
} from "@/lib/account/account-schemas";
import {
  AccountRequestError,
  patchAccountProfile,
  postChangeAccountPassword,
  putAccountSocials,
} from "@/lib/account/account-api";
import { accountKeys, fetchAccountProfile, fetchAccountSocialPlatforms } from "@/lib/account/account-query";
import { applyApiFieldErrorsToForm, getApiErrorMessage, parseApiErrorBody } from "@/lib/http/api-errors";
import { buildSocialUrlsFromProfile } from "@/lib/account/social-links-account";
import {
  AccountProfileCardSkeleton,
  SocialLinksFieldsSkeleton,
} from "@/components/skeletons/account-settings-skeleton";

type AccountSection = "profile" | "password";

const PASSWORD_FORM_EMPTY: ChangePasswordFormValues = {
  currentPassword: "",
  newPassword: "",
  confirmPassword: "",
};

export function AccountSettingsForm() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const profileQuery = useQuery({
    queryKey: accountKeys.me(),
    queryFn: fetchAccountProfile,
    retry: false,
  });

  const profile = profileQuery.data ?? null;

  const socialPlatformsQuery = useQuery({
    queryKey: accountKeys.socialPlatforms(),
    queryFn: fetchAccountSocialPlatforms,
    enabled: Boolean(profile?.id),
  });

  const socialPlatforms = useMemo(
    () => socialPlatformsQuery.data ?? [],
    [socialPlatformsQuery.data],
  );
  const socialPlatformsLoading = socialPlatformsQuery.isPending;
  const socialPlatformsError = socialPlatformsQuery.isError
    ? socialPlatformsQuery.error instanceof Error
      ? socialPlatformsQuery.error.message
      : "Could not load social platforms."
    : null;

  const [section, setSection] = useState<AccountSection>("profile");
  const lastSavedRef = useRef<AccountProfileFormValues | null>(null);
  const profileHydratedRef = useRef<string | null>(null);

  const [socialUrls, setSocialUrls] = useState<Record<string, string>>({});

  const profileForm = useForm<AccountProfileFormValues>({
    resolver: zodResolver(accountProfileFormSchema),
    defaultValues: {
      username: "",
      bio: "",
      avatarUrl: "",
      isPublicProfile: true,
    },
    shouldUnregister: false,
  });

  const passwordForm = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordFormSchema),
    defaultValues: {
      currentPassword: "",
      newPassword: "",
      confirmPassword: "",
    },
    shouldUnregister: false,
  });

  const regUsername = profileForm.register("username");
  const regBio = profileForm.register("bio");
  const regAvatarUrl = profileForm.register("avatarUrl");
  const regPwdCurrent = passwordForm.register("currentPassword");
  const regPwdNew = passwordForm.register("newPassword");
  const regPwdConfirm = passwordForm.register("confirmPassword");

  const watchedUsername = useWatch({ control: profileForm.control, name: "username", defaultValue: "" });
  const watchedBio = useWatch({ control: profileForm.control, name: "bio", defaultValue: "" });
  const watchedAvatarUrl = useWatch({ control: profileForm.control, name: "avatarUrl", defaultValue: "" });
  const watchedIsPublicProfile = useWatch({
    control: profileForm.control,
    name: "isPublicProfile",
    defaultValue: true,
  });

  const watchedPwdCurrent = useWatch({
    control: passwordForm.control,
    name: "currentPassword",
    defaultValue: "",
  });
  const watchedPwdNew = useWatch({
    control: passwordForm.control,
    name: "newPassword",
    defaultValue: "",
  });
  const watchedPwdConfirm = useWatch({
    control: passwordForm.control,
    name: "confirmPassword",
    defaultValue: "",
  });

  useEffect(() => {
    if (profileQuery.isError && profileQuery.error instanceof Error && profileQuery.error.message === "UNAUTHORIZED") {
      router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
    }
  }, [profileQuery.isError, profileQuery.error, router]);

  useEffect(() => {
    if (!profile) {
      profileHydratedRef.current = null;
      setSocialUrls({});
      return;
    }
    if (profileHydratedRef.current !== profile.id) {
      profileHydratedRef.current = profile.id;
      const values: AccountProfileFormValues = {
        username: profile.username,
        bio: profile.bio ?? "",
        avatarUrl: profile.avatarUrl ?? "",
        isPublicProfile: profile.isPublicProfile,
      };
      lastSavedRef.current = values;
      profileForm.reset(values);
      setSocialUrls({});
    }
  }, [profile, profileForm]);

  useEffect(() => {
    if (!profile || socialPlatforms.length === 0) {
      return;
    }
    setSocialUrls(buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks));
  }, [profile, socialPlatforms]);

  const socialBaseline = useMemo(() => {
    if (!profile || socialPlatforms.length === 0) {
      return {} as Record<string, string>;
    }
    return buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks);
  }, [profile, socialPlatforms]);

  const isSocialDirty = useMemo(() => {
    const keys = new Set([...Object.keys(socialUrls), ...Object.keys(socialBaseline)]);
    for (const k of keys) {
      if ((socialUrls[k] ?? "").trim() !== (socialBaseline[k] ?? "").trim()) {
        return true;
      }
    }
    return false;
  }, [socialUrls, socialBaseline]);

  const canPersistSocialLinks =
    socialPlatforms.length > 0 && !socialPlatformsLoading && !socialPlatformsError;

  /** Build PUT body; on invalid input shows toast and returns null. */
  const patchProfileMutation = useMutation({
    mutationFn: patchAccountProfile,
  });

  const putSocialsMutation = useMutation({
    mutationFn: putAccountSocials,
  });

  const changePasswordMutation = useMutation({
    mutationFn: postChangeAccountPassword,
    onSuccess: () => {
      passwordForm.reset(PASSWORD_FORM_EMPTY);
      toast.success("Password updated.");
      setSection("profile");
    },
    onError: (err: unknown) => {
      if (err instanceof AccountRequestError) {
        if (err.status === 401) {
          router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
          return;
        }
        const parsed = parseApiErrorBody(err.body);
        if (parsed?.fieldErrors && Object.keys(parsed.fieldErrors).length > 0) {
          applyApiFieldErrorsToForm(passwordForm.setError, parsed.fieldErrors);
        }
        toast.error(getApiErrorMessage(err.body, "Could not change password."));
        return;
      }
      toast.error("Network error. Try again.");
    },
  });

  const savingProfile = patchProfileMutation.isPending || putSocialsMutation.isPending;

  const tryBuildSocialLinksPayload = useCallback((): Array<{ platformId: string; url: string }> | null => {
    const links: Array<{ platformId: string; url: string }> = [];
    for (const p of socialPlatforms) {
      const raw = (socialUrls[p.id] ?? "").trim();
      if (!raw) continue;
      if (raw.length > 500) {
        toast.error(`${p.name}: URL must be at most 500 characters.`);
        return null;
      }
      try {
        const u = new URL(raw);
        if (u.protocol !== "http:" && u.protocol !== "https:") {
          toast.error(`${p.name}: Use an http or https URL.`);
          return null;
        }
      } catch {
        toast.error(`${p.name}: Enter a valid URL or leave empty.`);
        return null;
      }
      links.push({ platformId: p.id, url: raw });
    }
    const ids = new Set(links.map((l) => l.platformId));
    if (ids.size !== links.length) {
      toast.error("Each platform can only appear once.");
      return null;
    }
    return links;
  }, [socialPlatforms, socialUrls]);

  const onSaveProfile = profileForm.handleSubmit(async (values) => {
    const dirtyProfile = profileForm.formState.isDirty;
    const dirtySocial = isSocialDirty;
    if (!dirtyProfile && !dirtySocial) {
      return;
    }
    if (dirtySocial && !canPersistSocialLinks) {
      toast.error("Social platforms are not ready. Fix loading errors or try again.");
      return;
    }

    try {
      if (dirtyProfile) {
        try {
          const data = await patchProfileMutation.mutateAsync(values);
          queryClient.setQueryData<AccountProfile>(accountKeys.me(), (prev) =>
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
        } catch (err) {
          if (err instanceof AccountRequestError) {
            if (err.status === 401) {
              router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
              return;
            }
            const parsed = parseApiErrorBody(err.body);
            if (parsed?.fieldErrors && Object.keys(parsed.fieldErrors).length > 0) {
              applyApiFieldErrorsToForm(profileForm.setError, parsed.fieldErrors);
            }
            toast.error(getApiErrorMessage(err.body, "Could not save profile."));
            return;
          }
          toast.error("Network error. Try again.");
          return;
        }
      }

      if (dirtySocial) {
        const links = tryBuildSocialLinksPayload();
        if (links === null) {
          return;
        }
        try {
          const updated = await putSocialsMutation.mutateAsync(links);
          queryClient.setQueryData<AccountProfile>(accountKeys.me(), (prev) =>
            prev ? { ...prev, socialLinks: updated } : prev,
          );
          setSocialUrls(buildSocialUrlsFromProfile(socialPlatforms, updated));
        } catch (err) {
          if (err instanceof AccountRequestError) {
            if (err.status === 401) {
              router.push(`/login?returnUrl=${encodeURIComponent("/account")}`);
              return;
            }
            toast.error(getApiErrorMessage(err.body, "Could not save social links."));
            return;
          }
          toast.error("Network error. Try again.");
          return;
        }
      }

      toast.success("Changes saved.");
      router.refresh();
    } catch {
      toast.error("Network error. Try again.");
    }
  });

  const onCancelProfile = () => {
    const snap = lastSavedRef.current;
    if (snap) profileForm.reset(snap);
    if (profile && socialPlatforms.length > 0) {
      setSocialUrls(buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks));
    }
  };

  const hasProfileOrSocialChanges = profileForm.formState.isDirty || isSocialDirty;
  const saveBlockedBySocial =
    isSocialDirty && !canPersistSocialLinks;

  const openPasswordSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY);
    setSection("password");
  };

  const backToProfileSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY);
    setSection("profile");
  };

  const onChangePassword = passwordForm.handleSubmit((values) => changePasswordMutation.mutate(values));

  if (profileQuery.isPending && !profile) {
    return <AccountProfileCardSkeleton />;
  }

  if (profileQuery.isError && !profile) {
    return (
      <Card className="border-border bg-card-elevated">
        <CardHeader>
          <CardTitle className="text-xl">Account</CardTitle>
          <CardDescription>We could not load your profile.</CardDescription>
        </CardHeader>
        <CardContent>
          <p className="text-destructive text-sm">
            {profileQuery.error instanceof Error ? profileQuery.error.message : "Network error. Try again."}
          </p>
          <Button type="button" variant="outline" className="mt-4" onClick={() => void profileQuery.refetch()}>
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
              <Input
                id="account-email"
                value={profile?.email == null ? "" : String(profile.email)}
                readOnly
                disabled
                className="bg-muted/50"
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-username">Username</Label>
              <Input
                id="account-username"
                autoComplete="username"
                name={regUsername.name}
                ref={regUsername.ref}
                onBlur={regUsername.onBlur}
                onChange={regUsername.onChange}
                value={watchedUsername ?? ""}
              />
              {profileForm.formState.errors.username && (
                <p className="text-destructive text-sm">{profileForm.formState.errors.username.message}</p>
              )}
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-bio">Bio</Label>
              <Textarea
                id="account-bio"
                className="bg-input border-border"
                name={regBio.name}
                ref={regBio.ref}
                onBlur={regBio.onBlur}
                onChange={regBio.onChange}
                value={watchedBio ?? ""}
              />
              {profileForm.formState.errors.bio && (
                <p className="text-destructive text-sm">{profileForm.formState.errors.bio.message}</p>
              )}
            </div>

            <div className="space-y-2">
              <Label htmlFor="account-avatar">Avatar URL</Label>
              <Input
                id="account-avatar"
                type="url"
                placeholder="https://…"
                name={regAvatarUrl.name}
                ref={regAvatarUrl.ref}
                onBlur={regAvatarUrl.onBlur}
                onChange={regAvatarUrl.onChange}
                value={watchedAvatarUrl ?? ""}
              />
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
                checked={watchedIsPublicProfile ?? true}
                onCheckedChange={(v) => profileForm.setValue("isPublicProfile", v, { shouldDirty: true })}
              />
            </div>

            <div className="space-y-4 border-t border-border/60 pt-6">
              <div>
                <p className="text-sm font-medium text-foreground">Social links</p>
                <p className="text-muted-foreground text-xs mt-0.5">
                  Shown on your public profile. Leave blank to remove a link. Must be full{" "}
                  <span className="font-mono">http://</span> or <span className="font-mono">https://</span> URLs.
                </p>
              </div>
              {socialPlatformsLoading && <SocialLinksFieldsSkeleton rows={4} />}
              {socialPlatformsError && (
                <p className="text-destructive text-sm" role="alert">
                  {socialPlatformsError}
                </p>
              )}
              {!socialPlatformsLoading && !socialPlatformsError && socialPlatforms.length === 0 && profile && (
                <p className="text-sm text-muted-foreground">No social platforms are available.</p>
              )}
              <div className="space-y-3">
                {[...socialPlatforms]
                  .sort((a, b) => a.name.localeCompare(b.name))
                  .map((p) => (
                    <div key={p.id} className="space-y-1.5">
                      <Label htmlFor={`social-${p.id}`} className="text-xs font-medium">
                        {p.name}
                      </Label>
                      <Input
                        id={`social-${p.id}`}
                        type="url"
                        placeholder="https://…"
                        autoComplete="off"
                        value={socialUrls[p.id] ?? ""}
                        onChange={(e) =>
                          setSocialUrls((prev) => ({ ...prev, [p.id]: e.target.value }))
                        }
                        disabled={socialPlatformsLoading || savingProfile}
                        className="bg-input border-border"
                      />
                    </div>
                  ))}
              </div>
            </div>

            <div className="flex flex-wrap gap-2">
              <Button
                type="submit"
                disabled={savingProfile || !hasProfileOrSocialChanges || saveBlockedBySocial}
              >
                {savingProfile ? "Saving…" : "Save changes"}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={onCancelProfile}
                disabled={!hasProfileOrSocialChanges}
              >
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
                name={regPwdCurrent.name}
                ref={regPwdCurrent.ref}
                onBlur={regPwdCurrent.onBlur}
                onChange={regPwdCurrent.onChange}
                value={watchedPwdCurrent ?? ""}
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
                name={regPwdNew.name}
                ref={regPwdNew.ref}
                onBlur={regPwdNew.onBlur}
                onChange={regPwdNew.onChange}
                value={watchedPwdNew ?? ""}
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
                name={regPwdConfirm.name}
                ref={regPwdConfirm.ref}
                onBlur={regPwdConfirm.onBlur}
                onChange={regPwdConfirm.onChange}
                value={watchedPwdConfirm ?? ""}
              />
              {passwordForm.formState.errors.confirmPassword && (
                <p className="text-destructive text-sm">{passwordForm.formState.errors.confirmPassword.message}</p>
              )}
            </div>
            <Button type="submit" disabled={changePasswordMutation.isPending}>
              {changePasswordMutation.isPending ? "Updating…" : "Update password"}
            </Button>
          </form>
        )}
      </CardContent>
    </Card>
  );
}
