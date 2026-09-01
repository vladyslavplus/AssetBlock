import { describe, expect, it } from 'vitest'
import {
  emailFieldSchema,
  loginFormSchema,
  passwordResetRequestSchema,
  registerFormSchema,
} from '@/lib/auth/schemas'

describe('emailFieldSchema', () => {
  it('accepts valid email addresses', () => {
    expect(emailFieldSchema.safeParse('user@example.com').success).toBe(true)
    expect(emailFieldSchema.safeParse('developer+test@domain.co.uk').success).toBe(true)
  })

  it('rejects empty string and invalid email formats', () => {
    const emptyResult = emailFieldSchema.safeParse('')
    expect(emptyResult.success).toBe(false)
    if (!emptyResult.success) {
      expect(emptyResult.error.issues[0]?.message).toMatch(/email is required/i)
    }

    const invalidResult = emailFieldSchema.safeParse('not-an-email')
    expect(invalidResult.success).toBe(false)
    if (!invalidResult.success) {
      expect(invalidResult.error.issues[0]?.message).toMatch(/valid email/i)
    }
  })

  it('rejects emails exceeding 256 characters', () => {
    const longEmail = `${'a'.repeat(250)}@test.com`
    const result = emailFieldSchema.safeParse(longEmail)
    expect(result.success).toBe(false)
  })
})

describe('passwordResetRequestSchema', () => {
  it('validates password reset request payload', () => {
    expect(passwordResetRequestSchema.safeParse({ email: 'user@example.com' }).success).toBe(true)
    expect(passwordResetRequestSchema.safeParse({ email: 'invalid' }).success).toBe(false)
    expect(passwordResetRequestSchema.safeParse({}).success).toBe(false)
  })
})

describe('loginFormSchema & registerFormSchema', () => {
  it('validates login form', () => {
    expect(
      loginFormSchema.safeParse({ email: 'user@test.com', password: 'secretpassword' }).success,
    ).toBe(true)
    expect(loginFormSchema.safeParse({ email: 'invalid', password: '' }).success).toBe(false)
  })

  it('validates register form and password confirmation', () => {
    expect(
      registerFormSchema.safeParse({
        username: 'alice',
        email: 'alice@test.com',
        password: 'password123',
        confirmPassword: 'password123',
      }).success,
    ).toBe(true)

    expect(
      registerFormSchema.safeParse({
        username: 'alice',
        email: 'alice@test.com',
        password: 'password123',
        confirmPassword: 'mismatchpassword',
      }).success,
    ).toBe(false)
  })
})
