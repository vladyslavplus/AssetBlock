export interface AccountProfileDto {
  id: string
  username: string
  email: string | null
  role?: string | null
  avatarUrl: string | null
  bio: string | null
  isPublicProfile: boolean
  createdAt: string
  socialLinks: Array<{
    id: string
    platformName: string
    iconName: string
    url: string
  }>
}

export type AccountProfile = AccountProfileDto

export interface UpdateUserProfileResponseDto {
  username: string
  avatarUrl: string | null
  bio: string | null
  isPublicProfile: boolean
}
