import type { Metadata } from 'next'

const siteName = 'AssetBlock'

export const defaultMetadata: Metadata = {
  title: {
    default: `${siteName} | Developer IP marketplace`,
    template: `%s | ${siteName}`,
  },
  description:
    'Discover and sell developer intellectual property: packages, templates, tools, and digital assets. Secure payments, encrypted delivery, and a marketplace built for builders.',
  applicationName: siteName,
  icons: {
    icon: [{ url: '/icon-dark-32x32.png' }, { url: '/icon.svg', type: 'image/svg+xml' }],
    apple: '/apple-icon.png',
  },
  openGraph: {
    type: 'website',
    siteName,
    title: `${siteName} | Developer IP marketplace`,
    description:
      'Buy and sell code packages, templates, and digital IP with secure checkout and encrypted delivery.',
  },
  twitter: {
    card: 'summary_large_image',
    title: `${siteName} | Developer IP marketplace`,
    description:
      'Buy and sell code packages, templates, and digital IP with secure checkout and encrypted delivery.',
  },
}
