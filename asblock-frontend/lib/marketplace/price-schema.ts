import { z } from 'zod'

export const MARKETPLACE_MAX_PRICE = 999_999.99

export const marketplacePriceSchema = z.coerce
  .number()
  .positive('Price must be greater than zero')
  .max(MARKETPLACE_MAX_PRICE, `Price must not exceed ${MARKETPLACE_MAX_PRICE.toFixed(2)}`)
  .refine((price) => Math.abs(Math.round(price * 100) - price * 100) < 1e-8, {
    message: 'Price must have at most two decimal places',
  })
