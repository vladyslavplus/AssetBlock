import { getApiErrorMessage } from "@/lib/http/api-errors";
import { buildCheckoutJsonBody } from "@/lib/payments/payments-client";
import type { CreateCheckoutResponse } from "@/lib/payments/payments-types";

export class CheckoutRequestError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "CheckoutRequestError";
    this.status = status;
  }
}

export async function postCreateCheckoutSession(assetId: string): Promise<CreateCheckoutResponse> {
  const res = await fetch("/api/payments/checkout", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(buildCheckoutJsonBody(assetId)),
  });
  const raw: unknown = res.headers.get("Content-Type")?.includes("application/json")
    ? await res.json()
    : await res.text();

  if (!res.ok) {
    const fallback = typeof raw === "string" ? raw : `Checkout failed (${res.status})`;
    throw new CheckoutRequestError(res.status, getApiErrorMessage(raw, fallback));
  }

  const data = raw as CreateCheckoutResponse;
  const url = data?.checkoutUrl?.trim();
  if (!url) {
    throw new CheckoutRequestError(res.status, "Checkout did not return a payment URL.");
  }
  return { ...data, checkoutUrl: url };
}
