import { z } from 'zod'

export const emailFieldSchema = z
  .string()
  .min(1, 'Email is required')
  .email('Enter a valid email')
  .max(256, 'Email must not exceed 256 characters')

export const passwordResetRequestSchema = z.object({
  email: emailFieldSchema,
})

export type PasswordResetRequestValues = z.infer<typeof passwordResetRequestSchema>

export const loginFormSchema = z.object({
  email: emailFieldSchema,
  password: z.string().min(1, 'Password is required'),
})

export type LoginFormValues = z.infer<typeof loginFormSchema>

export const registerFormSchema = z
  .object({
    username: z
      .string()
      .min(1, 'Username is required')
      .max(50, 'Username must not exceed 50 characters'),
    email: emailFieldSchema,
    password: z.string().min(8, 'Password must be at least 8 characters'),
    confirmPassword: z.string().min(1, 'Confirm your password'),
  })
  .refine((data) => data.password === data.confirmPassword, {
    message: 'Passwords do not match',
    path: ['confirmPassword'],
  })

export type RegisterFormValues = z.infer<typeof registerFormSchema>
