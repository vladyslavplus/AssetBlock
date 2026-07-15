'use client'

/* eslint-disable react-hooks/refs -- react-hook-form field refs and handleSubmit are valid here */

import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRouter } from 'next/navigation'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { toast } from 'sonner'

import {
  AccountRequestError,
  patchAccountProfile,
  postChangeAccountPassword,
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
import {
  applyApiFieldErrorsToForm,
  getApiErrorMessage,
  parseApiErrorBody,
} from '@/lib/http/api-errors'

export type AccountSection = 'profile' | 'password'

const PASSWORD_FORM_EMPTY: ChangePasswordFormValues = {
  currentPassword: '',
  newPassword: '',
  confirmPassword: '',
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
  const socialPlatforms = useMemo(
    () => socialPlatformsQuery.data ?? [],
    [socialPlatformsQuery.data],
  )
  const socialPlatformsLoading = socialPlatformsQuery.isPending
  const socialPlatformsError = socialPlatformsQuery.isError
    ? socialPlatformsQuery.error instanceof Error
      ? socialPlatformsQuery.error.message
      : 'Could not load social platforms.'
    : null

  const [section, setSection] = useState<AccountSection>('profile')
  const lastSavedRef = useRef<AccountProfileFormValues | null>(null)
  const profileHydratedRef = useRef<string | null>(null)
  const [socialUrls, setSocialUrls] = useState<Record<string, string>>({})

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
      setSocialUrls({})
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
      setSocialUrls({})
    }
  }, [profile, profileForm])

  useEffect(() => {
    if (profile && socialPlatforms.length > 0) {
      setSocialUrls(buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks))
    }
  }, [profile, socialPlatforms])

  const socialBaseline = useMemo(
    () =>
      profile && socialPlatforms.length > 0
        ? buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks)
        : {},
    [profile, socialPlatforms],
  )
  const isSocialDirty = useMemo(() => {
    const keys = new Set([...Object.keys(socialUrls), ...Object.keys(socialBaseline)])
    return [...keys].some(
      (key) => (socialUrls[key] ?? '').trim() !== (socialBaseline[key] ?? '').trim(),
    )
  }, [socialUrls, socialBaseline])
  const canPersistSocialLinks =
    socialPlatforms.length > 0 && !socialPlatformsLoading && !socialPlatformsError

  const patchProfileMutation = useMutation({ mutationFn: patchAccountProfile })
  const putSocialsMutation = useMutation({ mutationFn: putAccountSocials })
  const changePasswordMutation = useMutation({
    mutationFn: postChangeAccountPassword,
    onSuccess: () => {
      passwordForm.reset(PASSWORD_FORM_EMPTY)
      toast.success('Password updated.')
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
          applyApiFieldErrorsToForm(passwordForm.setError, parsed.fieldErrors)
        }
        toast.error(getApiErrorMessage(error.body, 'Could not change password.'))
        return
      }
      toast.error('Network error. Try again.')
    },
  })
  const savingProfile = patchProfileMutation.isPending || putSocialsMutation.isPending

  const tryBuildSocialLinksPayload = useCallback(() => {
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
  }, [socialPlatforms, socialUrls])

  const onSaveProfile = profileForm.handleSubmit(async (values) => {
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
          setSocialUrls(buildSocialUrlsFromProfile(socialPlatforms, updated))
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
  })

  const onCancelProfile = () => {
    if (lastSavedRef.current) profileForm.reset(lastSavedRef.current)
    if (profile && socialPlatforms.length > 0) {
      setSocialUrls(buildSocialUrlsFromProfile(socialPlatforms, profile.socialLinks))
    }
  }
  const openPasswordSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY)
    setSection('password')
  }
  const backToProfileSection = () => {
    passwordForm.reset(PASSWORD_FORM_EMPTY)
    setSection('profile')
  }
  const onChangePassword = passwordForm.handleSubmit((values) =>
    changePasswordMutation.mutate(values),
  )

  return {
    section,
    profile,
    profileQuery,
    socialPlatforms,
    socialPlatformsLoading,
    socialPlatformsError,
    socialUrls,
    setSocialUrls,
    profileForm,
    passwordForm,
    profileFields,
    passwordFields,
    profileValues,
    passwordValues,
    savingProfile,
    hasProfileOrSocialChanges: profileForm.formState.isDirty || isSocialDirty,
    saveBlockedBySocial: isSocialDirty && !canPersistSocialLinks,
    changingPassword: changePasswordMutation.isPending,
    onSaveProfile,
    onCancelProfile,
    onChangePassword,
    openPasswordSection,
    backToProfileSection,
  }
}

export type AccountSettingsController = ReturnType<typeof useAccountSettings>
