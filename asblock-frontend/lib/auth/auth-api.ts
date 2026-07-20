import type { LoginFormValues, RegisterFormValues } from '@/lib/auth/schemas'
import { getApiErrorMessage, parseApiErrorBody } from '@/lib/http/api-errors'

export class AuthRequestError extends Error {
  readonly status: number
  readonly body: unknown

  constructor(status: number, message: string, body: unknown = null) {
    super(message)
    this.name = 'AuthRequestError'
    this.status = status
    this.body = body
  }
}

export async function postAuthLogin(values: LoginFormValues): Promise<void> {
  const res = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(values),
  })
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new AuthRequestError(
      res.status,
      getApiErrorMessage(body, `Sign in failed (${res.status})`),
      body,
    )
  }
}

export async function postAuthRegister(values: RegisterFormValues): Promise<void> {
  const res = await fetch('/api/auth/register', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(values),
  })
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new AuthRequestError(
      res.status,
      getApiErrorMessage(body, `Registration failed (${res.status})`),
      body,
    )
  }
}

export async function postAuthLogout(): Promise<void> {
  await fetch('/api/auth/logout', { method: 'POST', credentials: 'include' })
}

export async function postPasswordResetRequest(email: string): Promise<void> {
  const res = await fetch('/api/auth/password-reset/request', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email }),
  })
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new AuthRequestError(
      res.status,
      getApiErrorMessage(body, `Could not send reset email (${res.status})`),
      body,
    )
  }
}

export async function postPasswordResetConfirm(token: string, newPassword: string): Promise<void> {
  const res = await fetch('/api/auth/password-reset/confirm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, newPassword }),
  })
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new AuthRequestError(
      res.status,
      getApiErrorMessage(body, `Could not reset password (${res.status})`),
      body,
    )
  }
}

export async function postEmailVerificationConfirm(token: string): Promise<void> {
  const res = await fetch('/api/auth/email-verification/confirm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  })
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new AuthRequestError(
      res.status,
      getApiErrorMessage(body, `Could not verify email (${res.status})`),
      body,
    )
  }
}

export async function postEmailChangeConfirm(token: string): Promise<void> {
  const res = await fetch('/api/auth/email-change/confirm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  })
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    throw new AuthRequestError(
      res.status,
      getApiErrorMessage(body, `Could not confirm email change (${res.status})`),
      body,
    )
  }
}

/** Extracts the stable error code from an AuthRequestError body, if present. */
export function getAuthErrorCode(err: AuthRequestError): string | undefined {
  return parseApiErrorBody(err.body)?.code
}
