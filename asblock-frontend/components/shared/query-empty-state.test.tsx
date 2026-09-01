import { render, screen } from '@testing-library/react'
import { BellOff } from 'lucide-react'
import { describe, expect, it } from 'vitest'
import { QueryEmptyState } from '@/components/shared/query-empty-state'

describe('QueryEmptyState', () => {
  it('renders title, description, icon and action', () => {
    render(
      <QueryEmptyState
        icon={BellOff}
        title="No notifications yet"
        description="Your inbox is empty."
        action={<button type="button">Refresh</button>}
      />,
    )
    expect(screen.getByText('No notifications yet')).toBeInTheDocument()
    expect(screen.getByText('Your inbox is empty.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument()
  })

  it('supports custom semantic headingLevel and compact mode', () => {
    render(<QueryEmptyState title="Custom heading" headingLevel="h2" compact />)
    const heading = screen.getByRole('heading', { level: 2, name: 'Custom heading' })
    expect(heading).toBeInTheDocument()
  })
})
