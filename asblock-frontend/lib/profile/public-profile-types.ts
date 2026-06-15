export interface UserSocialLinkPublic {
  id: string;
  platformName: string;
  iconName: string;
  url: string;
}

export interface UserProfilePublic {
  id: string;
  username: string;
  email?: string | null;
  avatarUrl?: string | null;
  bio?: string | null;
  isPublicProfile: boolean;
  createdAt: string;
  socialLinks: UserSocialLinkPublic[];
}
