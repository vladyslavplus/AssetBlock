import { getApiErrorMessage } from "@/lib/http/api-errors";
import type { PagedNotificationsDto } from "@/lib/notifications/notification-types";

export const NOTIFICATIONS_PAGE_SIZE = 4;

export const notificationsKeys = {
  all: ["account", "notifications"] as const,
  inbox: () => [...notificationsKeys.all, "inbox"] as const,
  unread: () => [...notificationsKeys.all, "unread"] as const,
};

export async function fetchNotificationsUnreadCount(): Promise<number> {
  const params = new URLSearchParams({
    page: "1",
    pageSize: "1",
    sortBy: "CreatedAt",
    sortDirection: "DESC",
    unreadOnly: "true",
  });
  const res = await fetch(`/api/account/notifications?${params}`, { credentials: "include", cache: "no-store" });
  if (!res.ok) {
    return 0;
  }
  const data = (await res.json()) as PagedNotificationsDto;
  return Number(data.totalCount) || 0;
}

export async function fetchNotificationsPage(
  page: number,
  pageSize: number,
): Promise<PagedNotificationsDto> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    sortBy: "CreatedAt",
    sortDirection: "DESC",
  });
  const res = await fetch(`/api/account/notifications?${params}`, { credentials: "include", cache: "no-store" });
  if (!res.ok) {
    throw new Error("Notifications request failed");
  }
  return (await res.json()) as PagedNotificationsDto;
}

export async function postMarkAllNotificationsRead(): Promise<{ updatedCount: number }> {
  const res = await fetch("/api/account/notifications/read-all", {
    method: "POST",
    credentials: "include",
  });
  const json: unknown = await res.json().catch(() => null);
  if (!res.ok) {
    throw new Error(getApiErrorMessage(json, `Mark all read failed (${res.status})`));
  }
  const data = json as { updatedCount?: number };
  return { updatedCount: Number(data.updatedCount) || 0 };
}

export async function patchNotificationUnread(id: string): Promise<void> {
  const res = await fetch(`/api/account/notifications/${encodeURIComponent(id)}/unread`, {
    method: "PATCH",
    credentials: "include",
  });
  const json: unknown = await res.json().catch(() => null);
  if (!res.ok) {
    throw new Error(getApiErrorMessage(json, `Mark unread failed (${res.status})`));
  }
}

export async function patchNotificationRead(id: string): Promise<void> {
  const res = await fetch(`/api/account/notifications/${encodeURIComponent(id)}/read`, {
    method: "PATCH",
    credentials: "include",
  });
  const json: unknown = await res.json().catch(() => null);
  if (!res.ok) {
    throw new Error(getApiErrorMessage(json, `Mark read failed (${res.status})`));
  }
}
