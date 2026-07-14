"use client";

import { useEffect, useRef, useSyncExternalStore } from "react";

const HEADER_HEIGHT_PX = 64;

export interface HeroPointerState {
  inside: boolean;
  targetX: number;
  targetY: number;
  smoothX: number;
  smoothY: number;
  lastX: number;
  lastY: number;
  speedX: number;
  speedY: number;
  width: number;
  height: number;
  clientX: number;
  clientY: number;
}

function subscribeReducedMotion(onChange: () => void) {
  const mq = window.matchMedia("(prefers-reduced-motion: reduce)");
  mq.addEventListener("change", onChange);
  return () => mq.removeEventListener("change", onChange);
}

function getReducedMotionSnapshot() {
  return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function getReducedMotionServerSnapshot() {
  return false;
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

export function useHeroInteraction(sectionRef: React.RefObject<HTMLElement | null>) {
  const pointerRef = useRef<HeroPointerState>({
    inside: false,
    targetX: 0,
    targetY: 0,
    smoothX: 0,
    smoothY: 0,
    lastX: 0,
    lastY: 0,
    speedX: 0,
    speedY: 0,
    width: 0,
    height: 0,
    clientX: 0,
    clientY: 0,
  });
  const prefersReducedMotion = useSyncExternalStore(
    subscribeReducedMotion,
    getReducedMotionSnapshot,
    getReducedMotionServerSnapshot,
  );

  useEffect(() => {
    if (prefersReducedMotion) {
      return;
    }

    const pointer = pointerRef.current;

    const syncMetrics = () => {
      const section = sectionRef.current;
      if (!section) {
        return;
      }

      const rect = section.getBoundingClientRect();
      pointer.width = Math.max(1, Math.round(rect.width));
      pointer.height = Math.max(1, Math.round(rect.height + HEADER_HEIGHT_PX));

      if (pointer.smoothX === 0 && pointer.smoothY === 0) {
        pointer.targetX = pointer.width * 0.58;
        pointer.targetY = pointer.height * 0.4;
        pointer.smoothX = pointer.targetX;
        pointer.smoothY = pointer.targetY;
        pointer.lastX = pointer.targetX;
        pointer.lastY = pointer.targetY;
      }
    };

    const handlePointerMove = (event: PointerEvent) => {
      const section = sectionRef.current;
      if (!section) {
        return;
      }

      const rect = section.getBoundingClientRect();
      const top = rect.top - HEADER_HEIGHT_PX;
      const height = rect.height + HEADER_HEIGHT_PX;
      const x = event.clientX - rect.left;
      const y = event.clientY - top;

      if (x < 0 || x > rect.width || y < 0 || y > height) {
        pointer.inside = false;
        return;
      }

      pointer.inside = true;
      pointer.targetX = clamp(x, 0, pointer.width);
      pointer.targetY = clamp(y, 0, pointer.height);
      pointer.clientX = event.clientX;
      pointer.clientY = event.clientY;
    };

    const handlePointerLeaveWindow = () => {
      pointer.inside = false;
    };

    syncMetrics();
    window.addEventListener("resize", syncMetrics);
    window.addEventListener("pointermove", handlePointerMove, { passive: true });
    window.addEventListener("pointerleave", handlePointerLeaveWindow);

    return () => {
      window.removeEventListener("resize", syncMetrics);
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerleave", handlePointerLeaveWindow);
    };
  }, [prefersReducedMotion, sectionRef]);

  return {
    pointerRef,
    prefersReducedMotion,
  };
}
