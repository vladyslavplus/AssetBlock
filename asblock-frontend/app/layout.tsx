import type { Metadata } from 'next'
import { Analytics } from '@vercel/analytics/next'
import { fontVariablesClassName } from '@/lib/fonts'
import { defaultMetadata } from '@/lib/site-metadata'
import './globals.css'

export const metadata: Metadata = defaultMetadata

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode
}>) {
  return (
    <html lang="en" className={fontVariablesClassName}>
      <body className="font-sans antialiased">
        {children}
        {process.env.NODE_ENV === 'production' && <Analytics />}
      </body>
    </html>
  )
}
