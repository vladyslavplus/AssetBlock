'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRouter } from 'next/navigation'
import { useEffect, useRef, useState, type SyntheticEvent } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'

import {
  AccountRequestError,
  patchAccountProfile,
  postChangeAccountPassword,
  postRequestEmailChange,
  postResendEmailVerification,
  putAccountSocials,
} from '@/lib/account/account-api'
import {
  accountKeys,
  fetchAccountProfile,
  fetchAccountSocialPlatforms,
} from '@/lib/account/account-query'
import {
  accountProfileFormSchema,
  type AccountProfileFormValues,
  changePasswordFormSchema,
  type ChangePasswordFormValues,
} from '@/lib/account/account-schemas'
import { buildSocialUrlsFromProfile } from '@/lib/account/social-links-account'
import type { AccountProfile } from '@/lib/account/account-types'
import { postAuthLogout } from '@/lib/auth/auth-api'
import {
  applyApiFieldErrorsToForm,
  getApiErrorMessage,
  parseApiErrorBody,
} from '@/lib/http/api-errors'

export type AccountSection = 'profile' | 'password' | 'email'

const emailChangeFormSchema = z.object({
  newEmail: z.string().min(1, 'Email is required').email('Enter a valid email address'),
  currentPassword: z.string().min(1, 'Current password is required'),
})
export type EmailChangeFormValues = z.infer<typeof emailChangeFormSchema>

const EMAIL_CHANGE_FORM_EMPTY: EmailChangeFormValues = { newEmail: '', currentPassword: '' }

const PASSWORD_FORM_EMPTY: ChangePasswordFormValues = {
  currentPassword: '',
  newPassword: '',
  confirmPassword: '',
}

const EMPTY_SOCIAL_PLATFORMS: Awaited<ReturnType<typeof fetchAccountSocialPlatforms>> = []

interface SocialDraft {
  profileId: string | null
  values: Record<string, string>
}

export function useAccountSettings() {
  const router = useRouter()
  const queryClient = useQueryClient()
  const profileQuery = useQuery({
    queryKey: accountKeys.me(),
    queryFn: fetchAccountProfile,
    retry: false,
  })
  const profile = profileQuery.data ?? null

  const socialPlatformsQuery = useQuery({
    queryKey: accountKeys.socialPlatforms(),
    queryFn: fetchAccountSocialPlatforms,
    enabled: Boolean(profile?.id),
  })
  const socialPlatforms = socialPlatformsQuery.data ?? EMPTY_SOCIAL_PLATFORMS
  const socialPlatformsLoading = socialPlatformsQuery.isPending
  const socialPlatformsError = socialPlatformsQuery.isError
    ? socialPlatformsQuery.error instanceof Error
      ? socialPlatformsQuery.error.message
      : 'Could not load social platforms.'
    : null

  const [section, setSection] = useState<AccountSection>('profile')
  const lastSavedRef = useRef<AccountProfileFormValues | null>(null)
  const profileHydratedRef = useRef<string | null>(null)
  const [socialDraft, setSocialDraft] = useState<SocialDraft>({ profileId: null, values: {} })
  const [resendCooldown, setResendCooldown] = useState(false)

  const profileForm = useForm<AccountProfileFormValues>({
    resolver: zodResolver(accountProfileFormSchema),
    defaultValues: { username: '', bio: '', avatarUrl: '', isPublicProfile: true },
    shouldUnregister: false,
  })
  const passwordForm = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordFormSchema),
    defaultValues: PASSWORD_FORM_EMPTY,
    shouldUnregister: false,
  })
  const emailChangeForm = useForm<EmailChangeFormValues>({
    resolver: zodResolver(emailChangeFormSchema),
    defaultValues: EMAIL_CHANGE_FORM_EMPTY,
    shouldUnregister: false,
  })

  const profileFields = {
    username: profileForm.register('username'),
    bio: profileForm.register('bio'),
    avatarUrl: profileForm.register('avatarUrl'),
  }
  const passwordFields = {
    currentPassword: passwordForm.register('currentPassword'),
    newPassword: passwordForm.register('newPassword'),
    confirmPassword: passwordForm.register('confirmPassword'),
  }
  const emailChangeFields = {
    newEmail: emailChangeForm.register('newEmail'),
    currentPassword: emailChangeForm.register('currentPassword'),
  }

  const profileValues = {
    username: useWatch({ control: profileForm.control, name: 'username', defaultValue: '' }),
    bio: useWatch({ control: profileForm.control, name: 'bio', defaultValue: '' }),
    avatarUrl: useWatch({ control: profileForm.control, name: 'avatarUrl', defaultValue: '' }),
    isPublicProfile: useWatch({
      control: profileForm.control,
      name: 'isPublicProfile',
      defaultValue: true,
    }),
  }
  const passwordValues = {
    currentPassword: useWatch({
      control: passwordForm.control,
      name: 'currentPassword',
      defaultValue: '',
    }),
    newPassword: useWatch({
      control: passwordForm.control,
      name: 'newPassword',
      defaultValue: '',
    }),
    confirmPassword: useWatch({
      control: passwordForm.control,
      name: 'confirmPassword',
      defaultValue: '',
    }),
  }
  const emailChangeValues = {
    newEmail: useWatch({ control: emailChangeForm.control, name: 'newEmail', defaultValue: '' }),
    currentPassword: useWatch({
      control: emailChangeForm.control,
      name: 'currentPassword',
      defaultValue: '',
    }),
  }

  useEffect(() => {
    if (
      profileQuery.isError &&
      profileQuery.error instanceof Error &&
      profileQuery.error.message === 'UNAUTHORIZED'
    ) {
      router.push(`/login?returnUrl=${encodeURIComponent('/account')}`)
    }
  }, [profileQuery.isError, profileQuery.error, router])

  useEffect(() => {
    if (!profile) {
      profileHydratedRef.current = null
      return
    }
    if (profileHydratedRef.current !== profile.id) {
      profileHydratedRef.current = profile.id
      const values: AccountProfileFormValues = {
        username: profile.username,
        bio: profile.bio ?? '',
        avatarUrl: profile.avatarUrl ?? '',
        isPublicProfile: profile.isPublicProfile,
      }
      lastSavedRef.current = values
      profileForm.reset(values)
    }
  }, [profile, profileForm])

  const socialBaseline =
    profile && socialPlatforms.length > 0
      ? buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks)
      : {}
  const socialOverrides = socialDraft.profileId === profile?.id ? socialDraft.values : {}
  const socialUrls = { ...socialBaseline, ...socialOverrides }
  const setSocialUrl = (platformId: string, url: string) => {
    setSocialDraft((previous) => ({
      profileId: profile?.id ?? null,
      values: {
        ...(previous.profileId === profile?.id ? previous.values : {}),
        [platformId]: url,
      },
    }))
  }
  const isSocialDirty = (() => {
    const keys = new Set([...Object.keys(socialUrls), ...Object.keys(socialBaseline)])
    return [...keys].some(
      (key) => (socialUrls[key] ?? '').trim() !== (socialBaseline[key] ?? '').trim(),
    )
  })()
  const canPersistSocialLinks =
    socialPlatforms.length > 0 && !socialPlatformsLoading && !socialPlatformsError

  const patchProfileMutation = useMutation({ mutationFn: patchAccountProfile })
  const putSocialsMutation = useMutation({ mutationFn: putAccountSocials })
  const changePasswordMutation = useMutation({
    mutationFn: postChangeAccountPassword,
    onSuccess: async () => {
      passwordForm.reset(PASSWORD_FORM_EMPTY)
      await postAuthLogout()
      router.push('/login?message=password-changed')
    },
    onError: (error: unknown) => {
      if (error instanceof AccountRequestError) {
        if (error.status === 401) {
          router.push(`/login?returnUrl=${encodeURIComponent('/account')}`)
          return
        }
        const parsed = parseApiErrorBody(error.body)
        if (parsed?.fieldErrors && Object.keys(parsed.fieldErrors).length > 0) {
          applyApiFieldErrorsToForm(passwordForm.setError, parsed.fieldErrors)
        }
        toast.error(getApiErrorMessage(error.body, 'Could not change password.'))
        return
      }
      toast.error('Network error. Try again.')
    },
  })

  const resendVerificationMutation = useMutation({
    mutationFn: postResendEmailVerification,
    onSuccess: () => {
      toast.success('Verification email sent. Check your inbox.')
      setResendCooldown(true)
      setTimeout(() => setResendCooldown(false), 60_000)
    },
    onError: (error: unknown) => {
      if (error instanceof AccountRequestError) {
        if (error.status === 401) {
          router.push(`/login?returnUrl=${encodeURIComponent('/account')}`)
          return
        }
        const parsed = parseApiErrorBody(error.body)
        if (parsed?.code === 'ERR_EMAIL_ACTION_COOLDOWN') {
          toast.error('Please wait before requesting another verification email.')
          setResendCooldown(true)
          setTimeout(() => setResendCooldown(false), 60_000)
          return
        }
        toast.error(getApiErrorMessage(error.body, 'Could not send verification email.'))
        return
      }
      toast.error('Network error. Try again.')
    },
  })

  const requestEmailChangeMutation = useMutation({
    mutationFn: (values: EmailChangeFormValues) =>
      postRequestEmailChange(values.newEmail, values.currentPassword),
    onSuccess: () => {
      emailChangeForm.reset(EMAIL_CHANGE_FORM_EMPTY)
      void queryClient.invalidateQueries({ queryKey: accountKeys.me() })
      toast.success('Email change requested. Check your new inbox for a confirmation link.')
      setSection('profile')
    },
    onError: (error: unknown) => {
      if (error instanceof AccountRequestError) {
        if (error.status === 401) {
          router.push(`/login?returnUrl=${encodeURIComponent('/account')}`)
          return
        }
        const parsed = parseApiErrorBody(error.body)
        if (parsed?.fieldErrors && Object.keys(parsed.fieldErrors).length > 0) {
          applyApiFieldErrorsToForm(emailChangeForm.setError, parsed.fieldErrors)
        }
        toast.error(getApiErrorMessage(error.body, 'Could not request email change.'))
        return
      }
      toast.error('Network error. Try again.')
    },
  })
  const savingProfile = patchProfileMutation.isPending || putSocialsMutation.isPending

  const tryBuildSocialLinksPayload = () => {
    const links: Array<{ platformId: string; url: string }> = []
    for (const platform of socialPlatforms) {
      const url = (socialUrls[platform.id] ?? '').trim()
      if (!url) continue
      if (url.length > 500) {
        toast.error(`${platform.name}: URL must be at most 500 characters.`)
        return null
      }
      try {
        const parsed = new URL(url)
        if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
          toast.error(`${platform.name}: Use an http or https URL.`)
          return null
        }
      } catch {
        toast.error(`${platform.name}: Enter a valid URL or leave empty.`)
        return null
      }
      links.push({ platformId: platform.id, url })
    }
    if (new Set(links.map((link) => link.platformId)).size !== links.length) {
      toast.error('Each platform can only appear once.')
      return null
    }
    return links
  }

  const saveProfile = async (values: AccountProfileFormValues) => {
    const dirtyProfile = profileForm.formState.isDirty
    const dirtySocial = isSocialDirty
    if (!dirtyProfile && !dirtySocial) return
    if (dirtySocial && !canPersistSocialLinks) {
      toast.error('Social platforms are not ready. Fix loading errors or try again.')
      return
    }

    try {
      if (dirtyProfile) {
        try {
          const data = await patchProfileMutation.mutateAsync(values)
          queryClient.setQueryData<AccountProfile>(accountKeys.me(), (previous) =>
            previous
              ? {
                  ...previous,
                  username: data.username,
                  avatarUrl: data.avatarUrl,
                  bio: data.bio,
                  isPublicProfile: data.isPublicProfile,
                }
              : previous,
          )
          const next: AccountProfileFormValues = {
            username: data.username,
            bio: data.bio ?? '',
            avatarUrl: data.avatarUrl ?? '',
            isPublicProfile: data.isPublicProfile,
          }
          lastSavedRef.current = next
          profileForm.reset(next)
        } catch (error) {
          if (error instanceof AccountRequestError) {
            if (error.status === 401) {
              router.push(`/login?returnUrl=${encodeURIComponent('/account')}`)
              return
            }
            const parsed = parseApiErrorBody(error.body)
            if (parsed?.fieldErrors && Object.keys(parsed.fieldErrors).length > 0) {
              applyApiFieldErrorsToForm(profileForm.setError, parsed.fieldErrors)
            }
            toast.error(getApiErrorMessage(error.body, 'Could not save profile.'))
            return
          }
          toast.error('Network error. Try again.')
          return
        }
      }

      if (dirtySocial) {
        const links = tryBuildSocialLinksPayload()
        if (links === null) return
        try {
          const updated = await putSocialsMutation.mutateAsync(links)
          queryClient.setQueryData<AccountProfile>(accountKeys.me(), (previous) =>
            previous ? { ...previous, socialLinks: updated } : previous,
          )
          setSocialDraft({ profileId: profile?.id ?? null, values: {} })
        } catch (error) {
          if (error instanceof AccountRequestError) {
            if (error.status === 401) {
              router.push(`/login?returnUrl=${encodeURIComponent('/account')}`)
              return
            }
            toast.error(getApiErrorMessage(error.body, 'Could not save social links.'))
            return
          }
          toast.error('Network error. Try again.')
          return
        }
      }

      toast.success('Changes saved.')
      router.refresh()
    } catch {
      toast.error('Network error. Try again.')
    }
  }

  const onSaveProfile = (event: SyntheticEvent<HTMLFormElement, SubmitEvent>) => {
    void profileForm.handleSubmit(saveProfile)(event)
  }

  const onCancelProfile = () => {
    if (lastSavedRef.current) profileForm.reset(lastSavedRef.current)
    setSocialDraft({ profileId: profile?.id ?? null, values: {} })
  }
  const openPasswordSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY)
    setSection('password')
  }
  const openEmailSection = () => {
    emailChangeForm.reset(EMAIL_CHANGE_FORM_EMPTY)
    setSection('email')
  }
  const backToProfileSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY)
    emailChangeForm.reset(EMAIL_CHANGE_FORM_EMPTY)
    setSection('profile')
  }
  const onChangePassword = passwordForm.handleSubmit((values) =>
    changePasswordMutation.mutate(values),
  )
  const onRequestEmailChange = emailChangeForm.handleSubmit((values) =>
    requestEmailChangeMutation.mutate(values),
  )
  const onResendVerification = () => resendVerificationMutation.mutate()

  return {
    section,
    profile,
    profileQuery,
    socialPlatforms,
    socialPlatformsLoading,
    socialPlatformsError,
    socialUrls,
    setSocialUrl,
    profileForm,
    passwordForm,
    emailChangeForm,
    profileFields,
    passwordFields,
    emailChangeFields,
    profileValues,
    passwordValues,
    emailChangeValues,
    savingProfile,
    hasProfileOrSocialChanges: profileForm.formState.isDirty || isSocialDirty,
    saveBlockedBySocial: isSocialDirty && !canPersistSocialLinks,
    changingPassword: changePasswordMutation.isPending,
    requestingEmailChange: requestEmailChangeMutation.isPending,
    resendingVerification: resendVerificationMutation.isPending,
    resendCooldown,
    onSaveProfile,
    onCancelProfile,
    onChangePassword,
    onRequestEmailChange,
    onResendVerification,
    openPasswordSection,
    openEmailSection,
    backToProfileSection,
  }
}

export type AccountSettingsController = ReturnType<typeof useAccountSettings>
