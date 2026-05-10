import { z } from "zod";

export const accountProfileFormSchema = z
  .object({
    username: z.string().min(1, "Username is required").max(50, "Max 50 characters"),
    bio: z.string().max(1000, "Bio must be at most 1000 characters"),
    avatarUrl: z.string().max(500, "URL too long"),
    isPublicProfile: z.boolean(),
  })
  .superRefine((data, ctx) => {
    const u = data.avatarUrl.trim();
    if (u.length > 0 && !z.string().url().safeParse(u).success) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Enter a valid URL or leave empty",
        path: ["avatarUrl"],
      });
    }
  });

export type AccountProfileFormValues = z.infer<typeof accountProfileFormSchema>;

export const changePasswordFormSchema = z
  .object({
    currentPassword: z.string().min(1, "Current password is required"),
    newPassword: z.string().min(8, "At least 8 characters"),
    confirmPassword: z.string().min(1, "Confirm your new password"),
  })
  .refine((d) => d.newPassword === d.confirmPassword, {
    message: "Passwords do not match",
    path: ["confirmPassword"],
  })
  .refine((d) => d.newPassword !== d.currentPassword, {
    message: "New password must differ from the current one",
    path: ["newPassword"],
  });

export type ChangePasswordFormValues = z.infer<typeof changePasswordFormSchema>;
