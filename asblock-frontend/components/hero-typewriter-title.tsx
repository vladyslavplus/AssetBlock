'use client'

import { startTransition, useEffect, useRef, useState, type CSSProperties } from 'react'

const LINE1 = 'The marketplace'
const LINE2_PREFIX = 'for '
const LINE2_ACCENT = 'developer IP'
const FULL_TITLE = `${LINE1} ${LINE2_PREFIX}${LINE2_ACCENT}`
const GLITCH_CHARS = ['$', '_', '0', '|', '#', '/']

/** Non-space indices for idle glitches (random position across full title). */
function buildIdleGlitchCandidates(): Array<
  | { scope: 'line1'; index: number }
  | { scope: 'prefix'; index: number }
  | { scope: 'accent'; index: number }
> {
  const out: Array<
    | { scope: 'line1'; index: number }
    | { scope: 'prefix'; index: number }
    | { scope: 'accent'; index: number }
  > = []
  for (let i = 0; i < LINE1.length; i++) {
    if (LINE1[i] !== ' ') {
      out.push({ scope: 'line1', index: i })
    }
  }
  for (let i = 0; i < LINE2_PREFIX.length; i++) {
    if (LINE2_PREFIX[i] !== ' ') {
      out.push({ scope: 'prefix', index: i })
    }
  }
  for (let i = 0; i < LINE2_ACCENT.length; i++) {
    if (LINE2_ACCENT[i] !== ' ') {
      out.push({ scope: 'accent', index: i })
    }
  }
  return out
}

const IDLE_GLITCH_CANDIDATES = buildIdleGlitchCandidates()

type IdleGlitchState =
  | { scope: 'line1'; index: number; char: string }
  | { scope: 'prefix'; index: number; char: string }
  | { scope: 'accent'; index: number; char: string }

function substituteAt(str: string, index: number, ch: string): string {
  if (index < 0 || index >= str.length) {
    return str
  }
  return str.slice(0, index) + ch + str.slice(index + 1)
}

function pickGlitchChar(): string {
  return GLITCH_CHARS[Math.floor(Math.random() * GLITCH_CHARS.length)] ?? '$'
}

function randomStepMs(): number {
  return 42 + Math.floor(Math.random() * 48)
}

interface HeroTypewriterTitleProps {
  prefersReducedMotion: boolean
  className?: string
  style?: CSSProperties
}

export function HeroTypewriterTitle({
  prefersReducedMotion,
  className,
  style,
}: HeroTypewriterTitleProps) {
  const [line1, setLine1] = useState(() => (prefersReducedMotion ? LINE1 : 'T'))
  const [line2Prefix, setLine2Prefix] = useState(() => (prefersReducedMotion ? LINE2_PREFIX : ''))
  const [line2Accent, setLine2Accent] = useState(() => (prefersReducedMotion ? LINE2_ACCENT : ''))
  const [typingDone, setTypingDone] = useState(prefersReducedMotion)
  const [idleGlitch, setIdleGlitch] = useState<IdleGlitchState | null>(null)
  const timeoutIdsRef = useRef<number[]>([])

  const clearAllTimeouts = () => {
    for (const id of timeoutIdsRef.current) {
      window.clearTimeout(id)
    }
    timeoutIdsRef.current = []
  }

  const schedule = (fn: () => void, ms: number) => {
    const id = window.setTimeout(() => {
      timeoutIdsRef.current = timeoutIdsRef.current.filter((x) => x !== id)
      fn()
    }, ms)
    timeoutIdsRef.current.push(id)
  }

  useEffect(() => {
    clearAllTimeouts()

    if (prefersReducedMotion) {
      startTransition(() => {
        setIdleGlitch(null)
        setLine1(LINE1)
        setLine2Prefix(LINE2_PREFIX)
        setLine2Accent(LINE2_ACCENT)
        setTypingDone(true)
      })
      return () => {
        clearAllTimeouts()
      }
    }

    startTransition(() => {
      setIdleGlitch(null)
      setLine1('T')
      setLine2Prefix('')
      setLine2Accent('')
      setTypingDone(false)
    })

    const runLine1 = (index: number) => {
      if (index > LINE1.length) {
        return
      }

      if (index === LINE1.length) {
        runLine2Prefix(0)
        return
      }

      const char = LINE1.charAt(index)
      const advance = () => {
        schedule(() => runLine1(index + 1), randomStepMs())
      }

      if (char !== ' ' && Math.random() < 0.34) {
        setLine1(LINE1.slice(0, index) + pickGlitchChar())
        schedule(
          () => {
            setLine1(LINE1.slice(0, index + 1))
            advance()
          },
          55 + Math.floor(Math.random() * 45),
        )
      } else {
        setLine1(LINE1.slice(0, index + 1))
        advance()
      }
    }

    const runLine2Prefix = (index: number) => {
      if (index > LINE2_PREFIX.length) {
        return
      }

      if (index === LINE2_PREFIX.length) {
        runLine2Accent(0)
        return
      }

      const charP = LINE2_PREFIX.charAt(index)
      const advanceP = () => {
        schedule(() => runLine2Prefix(index + 1), randomStepMs())
      }

      if (charP !== ' ' && Math.random() < 0.3) {
        setLine2Prefix(LINE2_PREFIX.slice(0, index) + pickGlitchChar())
        schedule(
          () => {
            setLine2Prefix(LINE2_PREFIX.slice(0, index + 1))
            advanceP()
          },
          55 + Math.floor(Math.random() * 45),
        )
      } else {
        setLine2Prefix(LINE2_PREFIX.slice(0, index + 1))
        advanceP()
      }
    }

    const runLine2Accent = (index: number) => {
      if (index > LINE2_ACCENT.length) {
        return
      }

      if (index === LINE2_ACCENT.length) {
        setTypingDone(true)
        return
      }

      const char = LINE2_ACCENT.charAt(index)
      const advance = () => {
        schedule(() => runLine2Accent(index + 1), randomStepMs())
      }

      if (char !== ' ' && Math.random() < 0.38) {
        setLine2Accent(LINE2_ACCENT.slice(0, index) + pickGlitchChar())
        schedule(
          () => {
            setLine2Accent(LINE2_ACCENT.slice(0, index + 1))
            advance()
          },
          55 + Math.floor(Math.random() * 45),
        )
      } else {
        setLine2Accent(LINE2_ACCENT.slice(0, index + 1))
        advance()
      }
    }

    schedule(() => runLine1(1), 140)

    return () => {
      clearAllTimeouts()
    }
  }, [prefersReducedMotion])

  useEffect(() => {
    if (!typingDone || prefersReducedMotion) {
      return
    }

    const pending: number[] = []
    let cancelled = false

    const scheduleNextIdle = () => {
      if (cancelled) {
        return
      }

      const delay = 1600 + Math.random() * 2200
      const outerId = window.setTimeout(() => {
        if (cancelled) {
          return
        }

        if (IDLE_GLITCH_CANDIDATES.length === 0 || Math.random() < 0.12) {
          scheduleNextIdle()
          return
        }

        const pick =
          IDLE_GLITCH_CANDIDATES[Math.floor(Math.random() * IDLE_GLITCH_CANDIDATES.length)]
        if (!pick) {
          scheduleNextIdle()
          return
        }

        setIdleGlitch({ ...pick, char: pickGlitchChar() })
        const innerId = window.setTimeout(
          () => {
            if (cancelled) {
              return
            }
            setIdleGlitch(null)
            scheduleNextIdle()
          },
          70 + Math.floor(Math.random() * 55),
        )
        pending.push(innerId)
      }, delay)
      pending.push(outerId)
    }

    scheduleNextIdle()

    return () => {
      cancelled = true
      for (const id of pending) {
        window.clearTimeout(id)
      }
    }
  }, [typingDone, prefersReducedMotion])

  let line1Shown = line1
  let prefixShown = line2Prefix
  let accentShown = line2Accent
  if (idleGlitch !== null) {
    if (idleGlitch.scope === 'line1') {
      line1Shown = substituteAt(line1, idleGlitch.index, idleGlitch.char)
    } else if (idleGlitch.scope === 'prefix') {
      prefixShown = substituteAt(line2Prefix, idleGlitch.index, idleGlitch.char)
    } else {
      accentShown = substituteAt(line2Accent, idleGlitch.index, idleGlitch.char)
    }
  }

  return (
    <h1 className={className} style={style} aria-label={FULL_TITLE} aria-busy={!typingDone}>
      <span className="relative block">
        <span className="block select-none opacity-0">
          <span className="block">{LINE1}</span>
          <span className="block">
            {LINE2_PREFIX}
            <span>{LINE2_ACCENT}</span>
          </span>
        </span>

        <span className="absolute inset-0 block" aria-hidden="true">
          <span className="block">{line1Shown}</span>
          <span className="block transition-opacity duration-300 ease-out">
            <span className={line2Prefix.length > 0 ? 'opacity-100' : 'opacity-0'}>
              {prefixShown || LINE2_PREFIX}
            </span>
            <span className="text-primary">{accentShown}</span>
          </span>
        </span>
      </span>
    </h1>
  )
}
