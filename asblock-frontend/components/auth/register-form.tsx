"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Eye, EyeOff, AlertCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { AuthRequestError, postAuthRegister } from "@/lib/auth/auth-api";
import { registerFormSchema, type RegisterFormValues } from "@/lib/auth/schemas";
import { syncQueryCacheAfterAuth } from "@/lib/query/query-sync-after-auth";

interface RegisterFormProps {
  formError?: string;
}

export function RegisterForm({ formError }: RegisterFormProps) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [submitError, setSubmitError] = useState<string>("");

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<RegisterFormValues>({
    resolver: zodResolver(registerFormSchema),
    defaultValues: {
      username: "",
      email: "",
      password: "",
      confirmPassword: "",
    },
  });

  const registerMutation = useMutation({
    mutationFn: postAuthRegister,
    onMutate: () => setSubmitError(""),
    onSuccess: async () => {
      await syncQueryCacheAfterAuth(queryClient);
      router.push("/assets");
      router.refresh();
    },
    onError: (err: unknown) => {
      if (err instanceof AuthRequestError) {
        setSubmitError(err.message);
        return;
      }
      setSubmitError("Network error. Try again.");
    },
  });

  const onSubmit = handleSubmit((values) => registerMutation.mutate(values));

  return (
    <form onSubmit={onSubmit} className="flex flex-col gap-3">
      {(formError || submitError) && (
        <Alert className="bg-destructive/10 border-destructive/30 py-2">
          <AlertCircle className="h-4 w-4 text-destructive shrink-0" />
          <AlertDescription className="text-destructive/90 text-xs">
            {formError || submitError}
          </AlertDescription>
        </Alert>
      )}

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="username" className="text-xs font-medium">
          Username
        </Label>
        <Input
          id="username"
          type="text"
          placeholder="yourname"
          autoComplete="username"
          className="bg-input border-border text-xs placeholder:text-muted-foreground/50 focus-visible:ring-primary focus-visible:ring-offset-background h-8"
          {...register("username")}
        />
        {errors.username && (
          <p className="text-xs text-destructive">{errors.username.message}</p>
        )}
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="register-email" className="text-xs font-medium">
          Email address
        </Label>
        <Input
          id="register-email"
          type="email"
          placeholder="you@example.com"
          autoComplete="email"
          className="bg-input border-border text-xs placeholder:text-muted-foreground/50 focus-visible:ring-primary focus-visible:ring-offset-background h-8"
          {...register("email")}
        />
        {errors.email && (
          <p className="text-xs text-destructive">{errors.email.message}</p>
        )}
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="register-password" className="text-xs font-medium">
          Password
        </Label>
        <div className="relative">
          <Input
            id="register-password"
            type={showPassword ? "text" : "password"}
            placeholder="••••••••"
            autoComplete="new-password"
            className="bg-input border-border text-xs placeholder:text-muted-foreground/50 pr-8 focus-visible:ring-primary focus-visible:ring-offset-background h-8"
            {...register("password")}
          />
          <button
            type="button"
            onClick={() => setShowPassword((v) => !v)}
            className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm p-0.5"
            aria-label={showPassword ? "Hide password" : "Show password"}
          >
            {showPassword ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
          </button>
        </div>
        {errors.password && (
          <p className="text-xs text-destructive">{errors.password.message}</p>
        )}
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="confirm-password" className="text-xs font-medium">
          Confirm password
        </Label>
        <div className="relative">
          <Input
            id="confirm-password"
            type={showConfirm ? "text" : "password"}
            placeholder="••••••••"
            autoComplete="new-password"
            className="bg-input border-border text-xs placeholder:text-muted-foreground/50 pr-8 focus-visible:ring-primary focus-visible:ring-offset-background h-8"
            {...register("confirmPassword")}
          />
          <button
            type="button"
            onClick={() => setShowConfirm((v) => !v)}
            className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary rounded-sm p-0.5"
            aria-label={showConfirm ? "Hide password" : "Show password"}
          >
            {showConfirm ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
          </button>
        </div>
        {errors.confirmPassword && (
          <p className="text-xs text-destructive">{errors.confirmPassword.message}</p>
        )}
      </div>

      <Button
        type="submit"
        disabled={registerMutation.isPending}
        className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-8 text-sm"
      >
        {registerMutation.isPending ? "Creating account…" : "Create account"}
      </Button>
    </form>
  );
}
