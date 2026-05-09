export interface LoginBody {
  email: string;
  password: string;
}

export interface RegisterBody {
  username: string;
  email: string;
  password: string;
}

export interface TokensResponse {
  accessToken: string;
  refreshToken: string;
  accessExpiresAt: string; // ISO
  refreshExpiresAt: string; // ISO
}

export interface SessionUser {
  id: string;
  username: string;
  avatarUrl: string | null;
  bio: string | null;
  isPublicProfile: boolean;
  createdAt: string;
  socialLinks: Array<{
    id: string;
    platformName: string;
    iconName: string;
    url: string;
  }>;
}

export interface SessionResponse {
  user: SessionUser | null;
}
