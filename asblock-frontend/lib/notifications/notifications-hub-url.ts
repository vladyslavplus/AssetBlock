import { getPublicApiBaseUrl } from "@/lib/http/api-config";

export function getNotificationsHubUrl(): string {
  const base = getPublicApiBaseUrl();
  return `${base}/hubs/notifications`;
}
