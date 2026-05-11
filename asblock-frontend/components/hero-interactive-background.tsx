"use client";

import { useEffect, useRef } from "react";
import type { HeroPointerState } from "@/components/hero-interaction";

interface Particle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  size: number;
  alpha: number;
  driftPhase: number;
  driftSpeed: number;
  driftAmount: number;
}

interface BurstParticle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  size: number;
  alpha: number;
  life: number;
  decay: number;
}

const HEADER_HEIGHT_PX = 64;
const DESKTOP_PARTICLE_COUNT = 136;
const MOBILE_PARTICLE_COUNT = 42;
const POINTER_RADIUS_PX = 180;
const POINTER_FORCE = 0.24;
const POINTER_VELOCITY_FORCE = 0.014;
const DRIFT_FORCE = 0.03;
const VELOCITY_DAMPING = 0.972;
const MAX_VELOCITY = 2.8;
const MOBILE_BREAKPOINT_PX = 768;
const BURST_PARTICLE_COUNT = 22;
const BURST_SPEED_MIN = 0.9;
const BURST_SPEED_MAX = 3.4;

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

function createParticles(width: number, height: number, count: number): Particle[] {
  return Array.from({ length: count }, () => ({
    x: Math.random() * width,
    y: Math.random() * height,
    vx: (Math.random() - 0.5) * 0.18,
    vy: (Math.random() - 0.5) * 0.18,
    size: 0.7 + Math.random() * 2.1,
    alpha: 0.18 + Math.random() * 0.42,
    driftPhase: Math.random() * Math.PI * 2,
    driftSpeed: 0.35 + Math.random() * 0.95,
    driftAmount: 0.7 + Math.random() * 1.25,
  }));
}

interface HeroInteractiveBackgroundProps {
  pointerRef: React.RefObject<HeroPointerState>;
  prefersReducedMotion: boolean;
  burstEventsRef: React.RefObject<Array<{ x: number; y: number }>>;
}

export function HeroInteractiveBackground({
  pointerRef,
  prefersReducedMotion,
  burstEventsRef,
}: HeroInteractiveBackgroundProps) {
  const regionRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const animationFrameRef = useRef<number | null>(null);
  const resizeObserverRef = useRef<ResizeObserver | null>(null);
  const visibilityPauseRef = useRef(false);
  const particlesRef = useRef<Particle[]>([]);
  const burstParticlesRef = useRef<BurstParticle[]>([]);
  const metricsRef = useRef({
    width: 0,
    height: 0,
    dpr: 1,
    particleCount: DESKTOP_PARTICLE_COUNT,
  });

  useEffect(() => {
    if (prefersReducedMotion) {
      return;
    }

    const region = regionRef.current;
    const canvas = canvasRef.current;
    if (!region || !canvas) {
      return;
    }

    const context = canvas.getContext("2d");
    if (!context) {
      return;
    }

    let lastFrameTime = performance.now();

    const resizeCanvas = () => {
      const rect = region.getBoundingClientRect();
      const width = Math.max(1, Math.round(rect.width));
      const height = Math.max(1, Math.round(rect.height));
      const dpr = clamp(window.devicePixelRatio || 1, 1, 2);
      const particleCount = window.innerWidth < MOBILE_BREAKPOINT_PX ? MOBILE_PARTICLE_COUNT : DESKTOP_PARTICLE_COUNT;

      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      canvas.style.width = `${width}px`;
      canvas.style.height = `${height}px`;
      context.setTransform(dpr, 0, 0, dpr, 0, 0);

      metricsRef.current = { width, height, dpr, particleCount };

      if (particlesRef.current.length !== particleCount) {
        particlesRef.current = createParticles(width, height, particleCount);
      }
    };

    const scheduleFrame = () => {
      if (animationFrameRef.current === null) {
        animationFrameRef.current = requestAnimationFrame(renderFrame);
      }
    };

    const handleVisibilityChange = () => {
      visibilityPauseRef.current = document.hidden;
      if (!document.hidden) {
        lastFrameTime = performance.now();
        scheduleFrame();
      }
    };

    const renderFrame = (timestamp: number) => {
      animationFrameRef.current = null;

      if (visibilityPauseRef.current) {
        return;
      }

      const { width, height } = metricsRef.current;
      if (width === 0 || height === 0) {
        return;
      }

      const elapsed = Math.min(32, timestamp - lastFrameTime);
      const step = elapsed / 16.6667;
      lastFrameTime = timestamp;

      const pointer = pointerRef.current;
      pointer.smoothX += (pointer.targetX - pointer.smoothX) * 0.11 * step;
      pointer.smoothY += (pointer.targetY - pointer.smoothY) * 0.11 * step;

      const deltaX = pointer.smoothX - pointer.lastX;
      const deltaY = pointer.smoothY - pointer.lastY;
      pointer.speedX = pointer.speedX * 0.78 + deltaX * 0.22;
      pointer.speedY = pointer.speedY * 0.78 + deltaY * 0.22;
      pointer.lastX = pointer.smoothX;
      pointer.lastY = pointer.smoothY;

      context.clearRect(0, 0, width, height);

      const pendingBursts = burstEventsRef.current;
      if (pendingBursts.length > 0) {
        for (const burst of pendingBursts) {
          for (let index = 0; index < BURST_PARTICLE_COUNT; index += 1) {
            const angle = Math.random() * Math.PI * 2;
            const speed = BURST_SPEED_MIN + Math.random() * (BURST_SPEED_MAX - BURST_SPEED_MIN);
            burstParticlesRef.current.push({
              x: burst.x,
              y: burst.y,
              vx: Math.cos(angle) * speed,
              vy: Math.sin(angle) * speed,
              size: 1 + Math.random() * 2.4,
              alpha: 0.3 + Math.random() * 0.4,
              life: 1,
              decay: 0.016 + Math.random() * 0.02,
            });
          }
        }
        pendingBursts.length = 0;
      }

      for (const particle of particlesRef.current) {
        const driftTheta = timestamp * 0.00042 * particle.driftSpeed + particle.driftPhase;
        const driftX =
          Math.cos(driftTheta) * 0.06 +
          Math.sin(driftTheta * 0.47 + particle.driftPhase) * 0.032;
        const driftY =
          Math.sin(driftTheta * 1.18) * 0.05 +
          Math.cos(driftTheta * 0.63 + particle.driftPhase * 0.8) * 0.028;
        particle.vx += driftX * DRIFT_FORCE * particle.driftAmount * step;
        particle.vy += driftY * DRIFT_FORCE * particle.driftAmount * step;

        if (pointer.inside) {
          const dx = particle.x - pointer.smoothX;
          const dy = particle.y - pointer.smoothY;
          const distance = Math.hypot(dx, dy);

          if (distance < POINTER_RADIUS_PX && distance > 0.001) {
            const influence = 1 - distance / POINTER_RADIUS_PX;
            const directionX = dx / distance;
            const directionY = dy / distance;
            const pointerVelocity = Math.hypot(pointer.speedX, pointer.speedY);
            const impulse = influence * influence * (POINTER_FORCE + pointerVelocity * POINTER_VELOCITY_FORCE);

            particle.vx += directionX * impulse * step;
            particle.vy += directionY * impulse * step;
          }
        }

        particle.vx *= Math.pow(VELOCITY_DAMPING, step);
        particle.vy *= Math.pow(VELOCITY_DAMPING, step);
        particle.vx = clamp(particle.vx, -MAX_VELOCITY, MAX_VELOCITY);
        particle.vy = clamp(particle.vy, -MAX_VELOCITY, MAX_VELOCITY);

        particle.x += particle.vx * step;
        particle.y += particle.vy * step;

        if (particle.x < -24) {
          particle.x = width + 24;
        } else if (particle.x > width + 24) {
          particle.x = -24;
        }

        if (particle.y < -24) {
          particle.y = height + 24;
        } else if (particle.y > height + 24) {
          particle.y = -24;
        }

        context.beginPath();
        context.fillStyle = `rgba(139, 92, 246, ${particle.alpha})`;
        context.arc(particle.x, particle.y, particle.size, 0, Math.PI * 2);
        context.fill();
      }

      if (burstParticlesRef.current.length > 0) {
        const nextBurstParticles: BurstParticle[] = [];

        for (const burstParticle of burstParticlesRef.current) {
          burstParticle.vx *= Math.pow(0.968, step);
          burstParticle.vy *= Math.pow(0.968, step);
          burstParticle.vy += 0.004 * step;
          burstParticle.x += burstParticle.vx * step;
          burstParticle.y += burstParticle.vy * step;
          burstParticle.life -= burstParticle.decay * step;

          if (burstParticle.life <= 0) {
            continue;
          }

          nextBurstParticles.push(burstParticle);
          context.beginPath();
          context.fillStyle = `rgba(167, 139, 250, ${burstParticle.alpha * burstParticle.life})`;
          context.arc(burstParticle.x, burstParticle.y, burstParticle.size * burstParticle.life, 0, Math.PI * 2);
          context.fill();
        }

        burstParticlesRef.current = nextBurstParticles;
      }

      scheduleFrame();
    };

    resizeCanvas();
    scheduleFrame();

    document.addEventListener("visibilitychange", handleVisibilityChange);

    resizeObserverRef.current = new ResizeObserver(() => {
      resizeCanvas();
      scheduleFrame();
    });
    resizeObserverRef.current.observe(region);

    return () => {
      document.removeEventListener("visibilitychange", handleVisibilityChange);
      resizeObserverRef.current?.disconnect();
      resizeObserverRef.current = null;
      if (animationFrameRef.current !== null) {
        cancelAnimationFrame(animationFrameRef.current);
        animationFrameRef.current = null;
      }
    };
  }, [burstEventsRef, pointerRef, prefersReducedMotion]);

  return (
    <div
      ref={regionRef}
      className="pointer-events-none absolute inset-x-0 top-0 z-0 h-[calc(100%+4rem)]"
      style={{ marginTop: `-${HEADER_HEIGHT_PX}px` }}
      aria-hidden="true"
    >
      <div
        className="absolute inset-0 opacity-[0.03]"
        style={{
          backgroundImage:
            "linear-gradient(to right, #9A96B0 1px, transparent 1px), linear-gradient(to bottom, #9A96B0 1px, transparent 1px)",
          backgroundSize: "40px 40px",
        }}
      />
      <div
        className="absolute inset-x-0 top-0 h-[72%] opacity-[0.12]"
        style={{
          background:
            "radial-gradient(ellipse at 50% 18%, rgba(124,58,237,0.38) 0%, rgba(124,58,237,0.12) 34%, rgba(124,58,237,0) 72%)",
        }}
      />
      <canvas ref={canvasRef} className="absolute inset-0 h-full w-full" />
    </div>
  );
}
