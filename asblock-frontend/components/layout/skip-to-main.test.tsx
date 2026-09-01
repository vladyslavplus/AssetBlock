import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import RootLayout from '@/app/layout'
import HomePage from '@/app/page'
import LoginPage from '@/app/login/page'
import { SiteMain } from '@/components/layout/site-main'

vi.mock('@/components/site-header', () => ({
  SiteHeader: () => <header data-testid="site-header" />,
}))
vi.mock('@/components/hero-section', () => ({ HeroSection: () => <div>Hero</div> }))
vi.mock('@/components/features-section', () => ({ FeaturesSection: () => <div>Features</div> }))
vi.mock('@/components/featured-assets-section', () => ({
  FeaturedAssetsSection: () => <div>Assets</div>,
}))
vi.mock('@/components/how-it-works-section', () => ({
  HowItWorksSection: () => <div>HowItWorks</div>,
}))
vi.mock('@/components/final-cta-section', () => ({ FinalCtaSection: () => <div>FinalCTA</div> }))
vi.mock('@/components/site-footer', () => ({ SiteFooter: () => <footer>Footer</footer> }))
vi.mock('@/components/auth/auth-card', () => ({ AuthCard: () => <div>AuthCard</div> }))

describe('Skip to main content accessibility', () => {
  it('renders skip link as the first focusable link targeting #main-content', async () => {
    const { container } = render(
      <RootLayout>
        <SiteMain>
          <h1>Test Content</h1>
        </SiteMain>
      </RootLayout>,
    )

    const skipLink = screen.getByRole('link', { name: /skip to main content/i })
    expect(skipLink).toBeInTheDocument()
    expect(skipLink).toHaveAttribute('href', '#main-content')
    expect(skipLink.className).toContain('sr-only')
    expect(skipLink.className).toContain('focus:not-sr-only')

    const mainElement = container.querySelector('#main-content')
    expect(mainElement).toBeInTheDocument()
    expect(mainElement).toHaveAttribute('tabIndex', '-1')
    expect(mainElement?.tagName.toLowerCase()).toBe('main')
  })

  it('allows keyboard focus on skip link and verifies focus target', async () => {
    const user = userEvent.setup()
    const homeNode = await HomePage()
    const { container } = render(<RootLayout>{homeNode}</RootLayout>)

    const skipLink = screen.getByRole('link', { name: /skip to main content/i })
    const mainTarget = container.querySelector('#main-content') as HTMLElement

    expect(skipLink).toBeInTheDocument()
    expect(mainTarget).toBeInTheDocument()

    await user.tab()
    expect(skipLink).toHaveFocus()

    mainTarget.focus()
    expect(mainTarget).toHaveFocus()
  })

  it.each([
    { name: 'HomePage', Component: HomePage },
    { name: 'LoginPage', Component: LoginPage },
  ])(
    'ensures real $name component renders valid <main id="main-content" tabIndex={-1}>',
    async ({ Component }) => {
      const node = await Component()
      const { container } = render(node)
      const mainTarget = container.querySelector('#main-content')
      expect(mainTarget).toBeInTheDocument()
      expect(mainTarget?.tagName.toLowerCase()).toBe('main')
      expect(mainTarget).toHaveAttribute('tabIndex', '-1')
    },
  )
})
