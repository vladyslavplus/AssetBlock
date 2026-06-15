import type { AccountProfile, UpdateUserProfileResponseDto } from "@/lib/account/account-types";
import type { AccountProfileFormValues, ChangePasswordFormValues } from "@/lib/account/account-schemas";
import { getApiErrorMessage } from "@/lib/http/api-errors";

export class AccountRequestError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(status: number, message: string, body: unknown = null) {
    super(message);
    this.name = "AccountRequestError";
    this.status = status;
    this.body = body;
  }
}

export async function patchAccountProfile(
  values: AccountProfileFormValues,
): Promise<UpdateUserProfileResponseDto> {
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
    throw new AccountRequestError(401, "UNAUTHORIZED", json);
  }
  if (!res.ok) {
    throw new AccountRequestError(
      res.status,
      getApiErrorMessage(json, "Could not save profile."),
      json,
    );
  }
  return json as UpdateUserProfileResponseDto;
}

export async function putAccountSocials(
  links: Array<{ platformId: string; url: string }>,
): Promise<AccountProfile["socialLinks"]> {
  const res = await fetch("/api/account/socials", {
    method: "PUT",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ links }),
  });
  const json: unknown = await res.json().catch(() => null);
  if (res.status === 401) {
    throw new AccountRequestError(401, "UNAUTHORIZED", json);
  }
  if (!res.ok) {
    throw new AccountRequestError(
      res.status,
      getApiErrorMessage(json, "Could not save social links."),
      json,
    );
  }
  if (!Array.isArray(json)) {
    throw new AccountRequestError(res.status, "Unexpected response from server.", json);
  }
  return json as AccountProfile["socialLinks"];
}

export async function postChangeAccountPassword(values: ChangePasswordFormValues): Promise<void> {
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
    throw new AccountRequestError(401, "UNAUTHORIZED", json);
  }
  if (!res.ok) {
    throw new AccountRequestError(
      res.status,
      getApiErrorMessage(json, "Could not change password."),
      json,
    );
  }
}
