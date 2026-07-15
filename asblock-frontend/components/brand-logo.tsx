import { cn } from '@/lib/utils'

interface BrandLogoProps {
  className?: string
  compact?: boolean
}

export function BrandLogo({ className, compact = false }: BrandLogoProps) {
  return (
    <span className={cn('inline-flex items-center', compact ? 'gap-2' : 'gap-2.5', className)}>
      <span
        className={cn(
          'relative inline-flex items-center justify-center rounded-md border border-primary/30 bg-primary/12 shadow-[0_0_24px_rgba(139,92,246,0.16)]',
          compact ? 'h-8 w-8' : 'h-9 w-9',
        )}
        aria-hidden="true"
      >
        <svg
          viewBox="0 0 40 40"
          className={
            compact ? 'h-5 w-5 translate-y-[0.5px]' : 'h-[1.35rem] w-[1.35rem] translate-y-[0.5px]'
          }
          fill="none"
          xmlns="http://www.w3.org/2000/svg"
        >
          <path
            d="M20 4.75L30.5 10.8V23.2L20 29.25L9.5 23.2V10.8L20 4.75Z"
            fill="#140F22"
            stroke="#8B5CF6"
            strokeWidth="1.85"
            strokeLinejoin="round"
          />
          <path
            d="M20 4.75L30.5 10.8L20 16.95L9.5 10.8L20 4.75Z"
            fill="#2A174B"
            stroke="#8B5CF6"
            strokeWidth="1.65"
            strokeLinejoin="round"
          />
          <path
            d="M9.5 10.8L20 16.95V29.25L9.5 23.2V10.8Z"
            fill="#1C1133"
            stroke="#8B5CF6"
            strokeWidth="1.65"
            strokeLinejoin="round"
          />
          <path
            d="M30.5 10.8L20 16.95V29.25L30.5 23.2V10.8Z"
            fill="#120C22"
            stroke="#8B5CF6"
            strokeWidth="1.65"
            strokeLinejoin="round"
          />
          <path
            d="M20 4.75V16.95M20 16.95V29.25M9.5 10.8L20 16.95L30.5 10.8"
            stroke="#A78BFA"
            strokeWidth="1.55"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
          <path
            d="M14.25 13.25L20 16.45L25.75 13.25"
            stroke="#DDD6FE"
            strokeWidth="1.45"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
          <path
            d="M14.9 20.15L18.3 22.1L14.9 23.95"
            stroke="#8B5CF6"
            strokeWidth="1.45"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
          <path
            d="M25.1 20.15L21.7 22.1L25.1 23.95"
            stroke="#8B5CF6"
            strokeWidth="1.45"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </span>
      <span
        className={cn(
          'font-semibold tracking-tight text-foreground',
          compact ? 'text-base' : 'text-[1.05rem]',
        )}
        style={{ fontFamily: 'var(--font-space-grotesk)' }}
      >
        AssetBlock
      </span>
    </span>
  )
}
