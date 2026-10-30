import { describe, expect, it } from 'vitest'
import { publishVersionFormSchema } from './seller-schemas'

describe('publishVersionFormSchema', () => {
  it('shows the license message when licenseCode is missing', () => {
    const file = new File(['archive'], 'asset.zip', { type: 'application/zip' })
    const result = publishVersionFormSchema.safeParse({ releaseNotes: 'Notes', file })

    expect(result.success).toBe(false)
    expect(result.error?.issues.find((issue) => issue.path[0] === 'licenseCode')?.message).toBe(
      'Select a license',
    )
  })
})
