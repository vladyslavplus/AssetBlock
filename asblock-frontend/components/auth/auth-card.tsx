"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { Suspense } from "react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { AuthFormSkeleton } from "@/components/skeletons/auth-form-skeleton";
import { SignInForm } from "./sign-in-form";
import { RegisterForm } from "./register-form";

export function AuthCard() {
  const pathname = usePathname();

  const isSignIn = pathname !== "/register";

  return (
    <div className="w-full max-w-md">
      <Card className="border-border bg-card-elevated">
        <CardHeader className="pb-3 pt-5 px-5">
          <div className="flex flex-col gap-2">
            <div>
              <CardTitle className="text-xl">
                {isSignIn ? "Welcome back" : "Get started"}
              </CardTitle>
              <CardDescription className="text-xs text-muted-foreground">
                {isSignIn
                  ? "Sign in to your AssetBlock account"
                  : "Create a new account to buy and sell"}
              </CardDescription>
            </div>

            <div className="flex gap-0 p-1 rounded-lg border border-border bg-secondary/30 mt-1">
              <Link
                href="/login"
                className={`flex-1 py-1.5 px-3 rounded-md font-medium text-xs text-center transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card ${
                  pathname === "/login" || isSignIn
                    ? "bg-primary text-primary-foreground shadow-sm"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                aria-selected={isSignIn}
              >
                Sign in
              </Link>
              <Link
                href="/register"
                className={`flex-1 py-1.5 px-3 rounded-md font-medium text-xs text-center transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-card ${
                  pathname === "/register" || !isSignIn
                    ? "bg-primary text-primary-foreground shadow-sm"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                aria-selected={!isSignIn}
              >
                Create account
              </Link>
            </div>
          </div>
        </CardHeader>

        <Separator className="bg-border/50" />

        <CardContent className="pt-4 pb-3 px-5">
          {isSignIn ? (
            <Suspense fallback={<AuthFormSkeleton />}>
              <SignInForm />
            </Suspense>
          ) : (
            <RegisterForm />
          )}

          <div className="mt-3 text-xs text-center">
            {isSignIn ? (
              <>
                <span className="text-muted-foreground">Don&apos;t have an account?{" "}</span>
                <Link
                  href="/register"
                  className="text-foreground font-medium hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
                >
                  Register
                </Link>
              </>
            ) : (
              <>
                <span className="text-muted-foreground">Already have an account?{" "}</span>
                <Link
                  href="/login"
                  className="text-foreground font-medium hover:text-accent transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
                >
                  Sign in
                </Link>
              </>
            )}
          </div>
        </CardContent>

        <div className="px-5 py-3 border-t border-border/30 space-y-2">
          <p className="text-xs text-muted-foreground text-center">
            Secure checkout on the marketplace is handled separately
          </p>
          <div className="text-center">
            <Link
              href="/"
              className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm"
            >
              ← Back to AssetBlock
            </Link>
          </div>
        </div>
      </Card>
    </div>
  );
}
