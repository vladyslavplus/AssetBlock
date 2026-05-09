"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import type { SessionResponse, SessionUser } from "@/lib/auth-types";

type AuthStatus = "loading" | "anonymous" | "authenticated";

interface AuthContextValue {
  user: SessionUser | null;
  status: AuthStatus;
  /** Re-fetch session from BFF (e.g. after login/register). */
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

async function fetchSession(): Promise<SessionUser | null> {
  const res = await fetch("/api/auth/session", { cache: "no-store" });
  if (!res.ok) {
    return null;
  }
  const body = (await res.json()) as SessionResponse;
  return body.user ?? null;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<SessionUser | null>(null);
  const [status, setStatus] = useState<AuthStatus>("loading");

  const refresh = useCallback(async () => {
    setStatus("loading");
    try {
      const next = await fetchSession();
      setUser(next);
      setStatus(next ? "authenticated" : "anonymous");
    } catch {
      setUser(null);
      setStatus("anonymous");
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const next = await fetchSession();
        if (cancelled) return;
        setUser(next);
        setStatus(next ? "authenticated" : "anonymous");
      } catch {
        if (cancelled) return;
        setUser(null);
        setStatus("anonymous");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const logout = useCallback(async () => {
    await fetch("/api/auth/logout", { method: "POST" });
    setUser(null);
    setStatus("anonymous");
  }, []);

  const value = useMemo(
    () => ({
      user,
      status,
      refresh,
      logout,
    }),
    [user, status, refresh, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth must be used within AuthProvider");
  }
  return ctx;
}
