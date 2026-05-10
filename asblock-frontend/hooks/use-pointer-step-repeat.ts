"use client";

import { useCallback, useEffect, useRef } from "react";

/** Delay before repeating after pointer down (ms). */
const HOLD_DELAY_MS = 420;
/** Repeat intervals get shorter as hold duration grows (ms). */
const REPEAT_DELAYS_MS = [95, 70, 48, 30, 20] as const;
const REPEAT_THRESHOLDS_MS = [0, 900, 2000, 3500, 5500] as const;

function pickRepeatDelay(elapsedMs: number): number {
  for (let i = REPEAT_THRESHOLDS_MS.length - 1; i >= 0; i--) {
    if (elapsedMs >= REPEAT_THRESHOLDS_MS[i]) {
      return REPEAT_DELAYS_MS[Math.min(i, REPEAT_DELAYS_MS.length - 1)];
    }
  }
  return REPEAT_DELAYS_MS[0];
}

/**
 * Pointer-down holds repeat the callback (same timing as catalog price steppers).
 * Use one instance per button so timers stay isolated.
 */
export function usePointerStepRepeat(onRepeat: () => void) {
  const onRepeatRef = useRef(onRepeat);
  useEffect(() => {
    onRepeatRef.current = onRepeat;
  }, [onRepeat]);

  const holdTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const repeatTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const repeatStartRef = useRef(0);

  const clearTimers = useCallback(() => {
    if (holdTimerRef.current !== null) {
      clearTimeout(holdTimerRef.current);
      holdTimerRef.current = null;
    }
    if (repeatTimerRef.current !== null) {
      clearTimeout(repeatTimerRef.current);
      repeatTimerRef.current = null;
    }
  }, []);

  useEffect(() => () => clearTimers(), [clearTimers]);

  const onPointerDown = useCallback(
    (disabled: boolean, e: React.PointerEvent<HTMLButtonElement>) => {
      if (disabled || e.button !== 0) return;
      e.preventDefault();
      e.currentTarget.setPointerCapture(e.pointerId);
      clearTimers();
      onRepeatRef.current();
      repeatStartRef.current = Date.now();
      holdTimerRef.current = setTimeout(() => {
        holdTimerRef.current = null;
        const scheduleTick = () => {
          onRepeatRef.current();
          const elapsed = Date.now() - repeatStartRef.current;
          repeatTimerRef.current = setTimeout(scheduleTick, pickRepeatDelay(elapsed));
        };
        scheduleTick();
      }, HOLD_DELAY_MS);
    },
    [clearTimers],
  );

  const onPointerEnd = useCallback(
    (e: React.PointerEvent<HTMLButtonElement>) => {
      try {
        if (e.currentTarget.hasPointerCapture(e.pointerId)) {
          e.currentTarget.releasePointerCapture(e.pointerId);
        }
      } catch {
        /* releasePointerCapture can throw if capture already cleared */
      }
      clearTimers();
    },
    [clearTimers],
  );

  return { onPointerDown, onPointerEnd };
}
