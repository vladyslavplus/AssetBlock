"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";
import type { z } from "zod";
import { adminDelete } from "@/lib/admin/admin-bff";
import { adminReviewDeleteSchema } from "@/lib/admin/admin-schemas";
import { catalogKeys } from "@/lib/catalog/catalog-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type Form = z.infer<typeof adminReviewDeleteSchema>;

export function AdminReviewsSection() {
  const queryClient = useQueryClient();
  const form = useForm<Form>({
    resolver: zodResolver(adminReviewDeleteSchema),
    defaultValues: { reviewId: "" },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => adminDelete(`/api/admin/reviews/${encodeURIComponent(id)}`),
    onSuccess: () => {
      toast.success("Review deleted.");
      form.reset({ reviewId: "" });
      void queryClient.invalidateQueries({ queryKey: catalogKeys.all });
    },
    onError: (e: Error) => toast.error(e.message),
  });

  return (
    <div className="space-y-4 max-w-md">
      <p className="text-sm text-muted-foreground">
        Remove an abusive or spam review by ID (from the API or database). This action cannot be undone.
      </p>
      <form
        className="space-y-3"
        onSubmit={form.handleSubmit((v) => deleteMutation.mutate(v.reviewId.trim()))}
      >
        <div className="space-y-1.5">
          <Label htmlFor="admin-review-id">Review ID (GUID)</Label>
          <Input
            id="admin-review-id"
            className="font-mono text-xs"
            placeholder="00000000-0000-0000-0000-000000000000"
            {...form.register("reviewId")}
          />
          {form.formState.errors.reviewId && (
            <p className="text-xs text-destructive">{form.formState.errors.reviewId.message}</p>
          )}
        </div>
        <Button
          type="submit"
          variant="destructive"
          size="sm"
          disabled={deleteMutation.isPending}
          className="gap-1.5"
        >
          <Trash2 className="size-3.5" aria-hidden />
          {deleteMutation.isPending ? "Deleting…" : "Delete review"}
        </Button>
      </form>
    </div>
  );
}
