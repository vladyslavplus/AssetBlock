import { redirect } from "next/navigation";

/** Backend StripeOptions default uses /payment/success; app UI lives under /checkout. */
export default function PaymentSuccessAliasPage() {
  redirect("/checkout/success");
}
