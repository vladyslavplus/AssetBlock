import type { Metadata } from 'next'

export const metadata: Metadata = {
  title: 'Browse assets · AssetBlock',
  description: 'Discover templates, tools, and digital assets on AssetBlock.',
}

export default function AssetsLayout({ children }: { children: React.ReactNode }) {
  return children
}
