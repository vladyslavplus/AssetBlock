export interface PaymentsCapabilities {
  checkoutConfigured: boolean
}

export interface CreateCheckoutResponse {
  checkoutUrl: string
  checkoutIntentId: string
}

export type CheckoutFulfillmentStatus = 'pending' | 'completed' | 'cancelled'

export interface CheckoutStatusResponse {
  status: CheckoutFulfillmentStatus
  checkoutIntentId: string
  orderId: string | null
  productTitle: string
  assetId: string | null
  bundleId: string | null
}
