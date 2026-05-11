import type { LoginFormValues, RegisterFormValues } from "@/lib/auth/schemas";
import { getApiErrorMessage } from "@/lib/http/api-errors";

export class AuthRequestError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "AuthRequestError";
    this.status = status;
  }
}

export async function postAuthLogin(values: LoginFormValues): Promise<void> {
  const res = await fetch("/api/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(values),
  });
  const body: unknown = await res.json().catch(() => null);
  if (!res.ok) {
    throw new AuthRequestError(res.status, getApiErrorMessage(body, `Sign in failed (${res.status})`));
  }
}

export async function postAuthRegister(values: RegisterFormValues): Promise<void> {
  const res = await fetch("/api/auth/register", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(values),
  });
  const body: unknown = await res.json().catch(() => null);
  if (!res.ok) {
    throw new AuthRequestError(res.status, getApiErrorMessage(body, `Registration failed (${res.status})`));
  }
}
