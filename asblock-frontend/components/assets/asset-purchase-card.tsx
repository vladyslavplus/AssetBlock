"use client";

import { Button } from "@/components/ui/button";
import { formatUsdWhole } from "@/lib/format-currency";
import { Lock, Zap, Download } from "lucide-react";

interface AssetPurchaseCardProps {
  title: string;
  price: number;
}

export function AssetPurchaseCard({ title, price }: AssetPurchaseCardProps) {
  return (
    <div className="rounded-lg border border-border bg-card-elevated p-5 flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <h3 className="font-semibold text-foreground text-sm line-clamp-2 text-balance">
          {title}
        </h3>
        <p className="text-2xl font-semibold font-mono text-foreground">
          {formatUsdWhole(price)}
        </p>
      </div>

      <Button
        type="button"
        className="bg-primary text-primary-foreground hover:bg-[#6D28D9] transition-smooth font-medium w-full h-10"
        onClick={() => console.info("[asset] Buy now clicked (wire Stripe checkout)")}
      >
        Buy now
      </Button>

      <div className="flex flex-col gap-2 pt-2 border-t border-border/50">
        <div className="flex items-center gap-2 text-xs">
          <Lock className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Secure checkout</span>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <Zap className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Instant delivery</span>
        </div>
        <div className="flex items-center gap-2 text-xs">
          <Download className="size-4 text-accent shrink-0" aria-hidden />
          <span className="text-muted-foreground leading-none">Lifetime access</span>
        </div>
      </div>
    </div>
  );
}
