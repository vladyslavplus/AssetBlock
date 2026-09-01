import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { AssetCard } from '@/components/assets/asset-card'
import type { AssetListItem } from '@/lib/catalog/asset-types'

const sampleAsset: AssetListItem = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  title: 'Fantasy Sword Model',
  description: 'High quality 3D sword mesh.',
  price: 25,
  categoryId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  categoryName: '3D Models',
  authorId: '11111111-1111-4111-8111-111111111111',
  authorUsername: 'creator',
  createdAt: '2026-01-01T00:00:00.000Z',
  tags: ['weapons', 'lowpoly'],
  averageRating: 4.5,
}

describe('AssetCard', () => {
  it('renders default grid variant', () => {
    render(<AssetCard asset={sampleAsset} />)
    expect(screen.getByText('Fantasy Sword Model')).toBeInTheDocument()
    expect(screen.getByText('$25')).toBeInTheDocument()
    expect(screen.getByText('3D Models')).toBeInTheDocument()
    expect(screen.getByText('@creator')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /view details/i })).toHaveAttribute(
      'href',
      '/assets/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa?src=catalog',
    )
  })

  it('renders carousel variant', () => {
    const { container } = render(<AssetCard asset={sampleAsset} variant="carousel" />)
    expect(screen.getByText('Fantasy Sword Model')).toBeInTheDocument()
    expect(container.querySelector('article')).toHaveClass('w-72')
  })
})
