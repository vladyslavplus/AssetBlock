import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { StarRating } from '@/components/assets/star-rating'

describe('StarRating', () => {
  it('renders accessible img role with clamped rating name out of 5', () => {
    render(<StarRating value={4.3} />)
    expect(screen.getByRole('img', { name: 'Rating: 4.3 out of 5' })).toBeInTheDocument()
    expect(screen.getByText('4.3')).toBeInTheDocument()
  })

  it('clamps values below 0 and above 5 with proper accessible role and name', () => {
    const { rerender } = render(<StarRating value={-2} />)
    expect(screen.getByRole('img', { name: 'Rating: 0.0 out of 5' })).toBeInTheDocument()

    rerender(<StarRating value={10} />)
    expect(screen.getByRole('img', { name: 'Rating: 5.0 out of 5' })).toBeInTheDocument()
  })
})
