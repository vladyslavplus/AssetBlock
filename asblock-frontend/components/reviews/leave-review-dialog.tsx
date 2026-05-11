"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Star } from "lucide-react";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { assetKeys } from "@/lib/catalog/asset-detail-query";
import { libraryKeys } from "@/lib/library/library-query";
import { ReviewRequestError, postAssetReview } from "@/lib/reviews/review-api";
import { leaveReviewFormSchema, type LeaveReviewFormValues } from "@/lib/reviews/review-schemas";

interface LeaveReviewDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  assetId: string;
  assetTitle: string;
  onSubmitted?: () => void;
}

export function LeaveReviewDialog({
  open,
  onOpenChange,
  assetId,
  assetTitle,
  onSubmitted,
}: LeaveReviewDialogProps) {
  const queryClient = useQueryClient();
  const { reset, control, setValue, register, handleSubmit, formState } = useForm<LeaveReviewFormValues>({
    resolver: zodResolver(leaveReviewFormSchema),
    defaultValues: { rating: 5, comment: "" },
  });

  useEffect(() => {
    if (open) {
      reset({ rating: 5, comment: "" });
    }
  }, [open, assetId, reset]);

  const rating = useWatch({ control, name: "rating", defaultValue: 5 });

  const reviewMutation = useMutation({
    mutationFn: (values: LeaveReviewFormValues) => postAssetReview(assetId, values),
    onSuccess: () => {
      toast.success("Thanks — your review was posted.");
      void queryClient.invalidateQueries({ queryKey: assetKeys.reviews(assetId) });
      void queryClient.invalidateQueries({ queryKey: libraryKeys.purchases() });
      onOpenChange(false);
      onSubmitted?.();
    },
    onError: (err: unknown) => {
      if (err instanceof ReviewRequestError) {
        if (err.status === 401) {
          toast.error("Sign in again to leave a review.");
          return;
        }
        toast.error(err.message);
        return;
      }
      toast.error("Network error. Try again.");
    },
  });

  const onSubmit = handleSubmit((values) => reviewMutation.mutate(values));

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Rate this purchase</DialogTitle>
          <DialogDescription className="line-clamp-3">
            <span className="font-medium text-foreground">{assetTitle}</span>
          </DialogDescription>
        </DialogHeader>

        <form className="space-y-4" onSubmit={onSubmit} noValidate>
          <div className="space-y-2">
            <Label>Rating</Label>
            <div className="flex gap-1" role="group" aria-label="Star rating">
              {[1, 2, 3, 4, 5].map((n) => (
                <button
                  key={n}
                  type="button"
                  className="rounded-md p-1 text-muted-foreground hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                  onClick={() => setValue("rating", n, { shouldValidate: true, shouldDirty: true })}
                  aria-pressed={rating === n}
                  aria-label={`${n} star${n === 1 ? "" : "s"}`}
                >
                  <Star
                    className={`size-8 ${n <= rating ? "fill-yellow-400 text-yellow-400" : "text-border"}`}
                  />
                </button>
              ))}
            </div>
            {formState.errors.rating && (
              <p className="text-destructive text-sm">{formState.errors.rating.message}</p>
            )}
          </div>

          <div className="space-y-2">
            <Label htmlFor="review-comment">Comment (optional)</Label>
            <Textarea
              id="review-comment"
              className="min-h-[100px] border-border bg-secondary text-foreground placeholder:text-muted-foreground dark:bg-secondary"
              placeholder="What did you think?"
              {...register("comment")}
            />
            {formState.errors.comment && (
              <p className="text-destructive text-sm">{formState.errors.comment.message}</p>
            )}
          </div>

          <DialogFooter className="gap-2 sm:gap-0">
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={reviewMutation.isPending}>
              {reviewMutation.isPending ? "Submitting…" : "Submit review"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
