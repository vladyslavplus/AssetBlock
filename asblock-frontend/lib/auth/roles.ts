export const APP_ROLE_ADMIN = "Admin" as const;
export const APP_ROLE_USER = "User" as const;

export type AppRole = typeof APP_ROLE_ADMIN | typeof APP_ROLE_USER;

export function isAdminRole(role: string | null | undefined): boolean {
  return role === APP_ROLE_ADMIN;
}
