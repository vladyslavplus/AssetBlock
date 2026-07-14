import type { AccountProfile } from "@/lib/account/account-types";

export interface SocialPlatformOption {
  id: string;
  name: string;
  iconName: string;
}

export type SavedSocialLinkItem = AccountProfile["socialLinks"][number];

/**
 * Maps known platforms to URL inputs; matches profile links by platform name + icon when possible.
 */
export function buildSocialUrlsFromProfile(
  platforms: SocialPlatformOption[],
  links: AccountProfile["socialLinks"],
): Record<string, string> {
  const next: Record<string, string> = {};
  for (const p of platforms) {
    next[p.id] = "";
  }
  for (const link of links) {
    const plat =
      platforms.find((x) => x.name === link.platformName && x.iconName === link.iconName) ??
      platforms.find((x) => x.name === link.platformName);
    if (plat) {
      next[plat.id] = link.url;
    }
  }
  return next;
}

export function parseSocialPlatformsResponse(json: unknown): SocialPlatformOption[] {
  if (!Array.isArray(json)) {
    return [];
  }
  const out: SocialPlatformOption[] = [];
  for (const row of json) {
    if (!row || typeof row !== "object") continue;
    const o = row as Record<string, unknown>;
    const id = typeof o.id === "string" ? o.id : null;
    const name = typeof o.name === "string" ? o.name : null;
    const iconName = typeof o.iconName === "string" ? o.iconName : null;
    if (id && name && iconName) {
      out.push({ id, name, iconName });
    }
  }
  return out;
}
