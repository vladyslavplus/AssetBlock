import { Geist, Geist_Mono, JetBrains_Mono, Space_Grotesk } from 'next/font/google'

export const fontGeist = Geist({
  subsets: ['latin'],
  variable: '--font-geist',
})

export const fontGeistMono = Geist_Mono({
  subsets: ['latin'],
  variable: '--font-geist-mono',
})

export const fontSpaceGrotesk = Space_Grotesk({
  subsets: ['latin'],
  variable: '--font-space-grotesk',
  weight: ['600'],
})

export const fontJetbrainsMono = JetBrains_Mono({
  subsets: ['latin'],
  variable: '--font-jetbrains-mono',
  weight: ['400', '500'],
})

export const fontVariablesClassName = [
  fontGeist.variable,
  fontGeistMono.variable,
  fontSpaceGrotesk.variable,
  fontJetbrainsMono.variable,
].join(' ')
