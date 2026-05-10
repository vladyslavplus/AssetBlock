"use client";

import Link from "next/link";
import { useEffect, useRef } from "react";
import { Button } from "@/components/ui/button";
import { ShieldCheck, Lock, Code2 } from "lucide-react";
import { HeroInteractiveBackground } from "@/components/hero-interactive-background";
import type { HeroPointerState } from "@/components/hero-interaction";
import { useHeroInteraction } from "@/components/hero-interaction";
import { HeroTypewriterTitle } from "@/components/hero-typewriter-title";
import { cn } from "@/lib/utils";
import { siteShellClass } from "@/lib/site-layout";

export function HeroSection() {
  const sectionRef = useRef<HTMLElement>(null);
  const { pointerRef, prefersReducedMotion } = useHeroInteraction(sectionRef);
  const burstEventsRef = useRef<Array<{ x: number; y: number }>>([]);

  useEffect(() => {
    const section = sectionRef.current;
    if (!section) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (event.button !== 0) {
        return;
      }

      const target = event.target as HTMLElement | null;
      if (
        target?.closest(
          [
            "a",
            "button",
            "input",
            "textarea",
            "select",
            "label",
            "[role='button']",
            "[data-no-particle-burst]",
          ].join(","),
        )
      ) {
        return;
      }

      const rect = section.getBoundingClientRect();
      const x = event.clientX - rect.left;
      const y = event.clientY - (rect.top - 64);

      if (x < 0 || x > rect.width || y < 0 || y > rect.height + 64) {
        return;
      }

      burstEventsRef.current.push({ x, y });
    };

    section.addEventListener("pointerdown", handlePointerDown);
    return () => {
      section.removeEventListener("pointerdown", handlePointerDown);
    };
  }, []);

  return (
    <section ref={sectionRef} className="relative overflow-hidden pt-32 pb-20 sm:pt-40 sm:pb-28">
      <HeroInteractiveBackground
        pointerRef={pointerRef}
        prefersReducedMotion={prefersReducedMotion}
        burstEventsRef={burstEventsRef}
      />
      <div className={cn("relative", siteShellClass("site"))}>
        <div className="grid lg:grid-cols-2 gap-12 items-center">
          <div className="flex flex-col gap-6 animate-fade-in" data-no-particle-burst="true">
            <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full border border-accent/30 bg-accent/5 w-fit">
              <span className="text-[11px] font-mono text-accent tracking-wider uppercase">
                v1.0 — Now open for sellers
              </span>
            </div>

            <HeroTypewriterTitle
              prefersReducedMotion={prefersReducedMotion}
              className="text-4xl sm:text-5xl lg:text-6xl font-semibold leading-[1.1] text-balance text-foreground"
              style={{ fontFamily: "var(--font-space-grotesk)" }}
            />

            <p className="text-base sm:text-lg text-muted-foreground leading-relaxed max-w-lg">
              Buy and sell code packages, templates, tools, and digital goods.
              Every transaction secured with encrypted delivery and
              developer-first licensing.
            </p>

            <div className="flex flex-wrap gap-3 pt-2">
              <Button
                size="lg"
                asChild
                className="bg-primary text-primary-foreground hover:bg-[#6D28D9] shadow-[0_0_24px_rgba(124,58,237,0.3)] hover:shadow-[0_0_30px_rgba(124,58,237,0.4)] transition-smooth"
              >
                <Link href="/assets">Browse assets</Link>
              </Button>
              <Button
                size="lg"
                variant="outline"
                asChild
                className="border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground transition-smooth"
              >
                <Link href="/sell">Become a seller</Link>
              </Button>
            </div>

            <div className="flex flex-wrap gap-5 pt-4 border-t border-border/50">
              <div className="flex items-center gap-2">
                <ShieldCheck className="w-4 h-4 text-accent shrink-0" />
                <span className="text-sm text-muted-foreground">Secure checkout</span>
              </div>
              <div className="flex items-center gap-2">
                <Lock className="w-4 h-4 text-accent shrink-0" />
                <span className="text-sm text-muted-foreground">Encrypted delivery</span>
              </div>
              <div className="flex items-center gap-2">
                <Code2 className="w-4 h-4 text-accent shrink-0" />
                <span className="text-sm text-muted-foreground">Developer-first</span>
              </div>
            </div>
          </div>

          <div
            className="relative hidden lg:block animate-fade-in-delay"
            aria-hidden="true"
            data-no-particle-burst="true"
          >
            <HeroIllustration pointerRef={pointerRef} prefersReducedMotion={prefersReducedMotion} />
          </div>
        </div>
      </div>
    </section>
  );
}

interface HeroIllustrationProps {
  pointerRef: React.RefObject<HeroPointerState>;
  prefersReducedMotion: boolean;
}

function HeroIllustration({ pointerRef, prefersReducedMotion }: HeroIllustrationProps) {
  const shellRef = useRef<HTMLDivElement>(null);
  const zoneRef = useRef<HTMLDivElement>(null);
  const rearCardRef = useRef<HTMLDivElement>(null);
  const midCardRef = useRef<HTMLDivElement>(null);
  const frontCardRef = useRef<HTMLDivElement>(null);
  const badgeRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (prefersReducedMotion) {
      return;
    }

    const shell = shellRef.current;
    const zone = zoneRef.current;
    const rearCard = rearCardRef.current;
    const midCard = midCardRef.current;
    const frontCard = frontCardRef.current;
    const badge = badgeRef.current;
    if (!shell || !zone || !rearCard || !midCard || !frontCard || !badge) {
      return;
    }

    let rafId = 0;
    let shellX = 0;
    let shellY = 0;
    let rearX = 0;
    let rearY = 0;
    let midX = 0;
    let midY = 0;
    let frontX = 0;
    let frontY = 0;
    let frontRotate = 0;
    let badgeX = 0;
    let badgeY = 0;
    let badgeRotate = 0;

    const animate = () => {
      const pointer = pointerRef.current;
      const zoneRect = zone.getBoundingClientRect();
      const extendedLeft = zoneRect.left - 84;
      const extendedRight = zoneRect.right + 84;
      const extendedTop = zoneRect.top - 72;
      const extendedBottom = zoneRect.bottom + 72;
      const insideZone =
        pointer.inside &&
        pointer.clientX >= extendedLeft &&
        pointer.clientX <= extendedRight &&
        pointer.clientY >= extendedTop &&
        pointer.clientY <= extendedBottom;

      if (insideZone && zoneRect.width > 0 && zoneRect.height > 0) {
        const normX = ((pointer.clientX - zoneRect.left) / zoneRect.width - 0.5) * 2;
        const normY = ((pointer.clientY - zoneRect.top) / zoneRect.height - 0.5) * 2;

        const targetShellX = normX * 4;
        const targetShellY = normY * 3;
        const targetRearX = normX * 3;
        const targetRearY = normY * 2;
        const targetMidX = normX * 6;
        const targetMidY = normY * 4;
        const targetFrontX = normX * 9;
        const targetFrontY = normY * 6;
        const targetFrontRotate = normX * 0.8;
        const targetBadgeX = normX * 10;
        const targetBadgeY = normY * 7;
        const targetBadgeRotate = normX * -0.55;

        shellX += (targetShellX - shellX) * 0.12;
        shellY += (targetShellY - shellY) * 0.12;
        rearX += (targetRearX - rearX) * 0.12;
        rearY += (targetRearY - rearY) * 0.12;
        midX += (targetMidX - midX) * 0.12;
        midY += (targetMidY - midY) * 0.12;
        frontX += (targetFrontX - frontX) * 0.12;
        frontY += (targetFrontY - frontY) * 0.12;
        frontRotate += (targetFrontRotate - frontRotate) * 0.12;
        badgeX += (targetBadgeX - badgeX) * 0.12;
        badgeY += (targetBadgeY - badgeY) * 0.12;
        badgeRotate += (targetBadgeRotate - badgeRotate) * 0.12;
      }

      shell.style.transform = `translate3d(${shellX}px, ${shellY}px, 0)`;
      rearCard.style.transform = `translate3d(${rearX}px, ${rearY}px, 0) rotate(3.4deg)`;
      midCard.style.transform = `translate3d(${midX}px, ${midY}px, 0) rotate(-1.3deg)`;
      frontCard.style.transform = `translate3d(${frontX}px, ${frontY}px, 0) rotate(${frontRotate}deg)`;
      badge.style.transform = `translate3d(${badgeX}px, ${badgeY}px, 0) rotate(${badgeRotate}deg)`;

      rafId = window.requestAnimationFrame(animate);
    };

    rafId = window.requestAnimationFrame(animate);

    return () => {
      window.cancelAnimationFrame(rafId);
    };
  }, [pointerRef, prefersReducedMotion]);

  return (
    <div ref={shellRef} className="relative flex items-center justify-center h-[440px] will-change-transform">
      <div ref={zoneRef} className="absolute right-0 top-2 h-[24.5rem] w-[34rem]" />
      <div
        ref={rearCardRef}
        className="absolute right-0 top-8 w-72 h-48 rounded-xl border border-border bg-card-elevated rotate-3 opacity-60"
        style={{ background: "#141322" }}
      />
      <div
        ref={midCardRef}
        className="absolute right-6 top-4 w-72 h-48 rounded-xl border border-border -rotate-1 opacity-80"
        style={{ background: "#11101A" }}
      >
        <div className="p-4 flex flex-col gap-2">
          <div className="flex gap-1.5">
            <div className="w-2.5 h-2.5 rounded-full bg-[#FB7185]/70" />
            <div className="w-2.5 h-2.5 rounded-full bg-yellow-400/50" />
            <div className="w-2.5 h-2.5 rounded-full bg-green-500/50" />
          </div>
          <div className="mt-1 flex flex-col gap-1.5">
            <div className="h-2 w-3/4 rounded bg-border/70" />
            <div className="h-2 w-1/2 rounded bg-primary/30" />
            <div className="h-2 w-2/3 rounded bg-border/70" />
            <div className="h-2 w-4/5 rounded bg-accent/20" />
            <div className="h-2 w-1/3 rounded bg-border/70" />
          </div>
        </div>
      </div>
      <div
        ref={frontCardRef}
        className="relative z-10 w-80 rounded-xl border border-border shadow-2xl shadow-primary/10"
        style={{ background: "#11101A" }}
      >
        <div className="p-5 flex flex-col gap-4">
          <div className="flex items-center justify-between">
            <span className="text-xs font-mono text-accent">react / template</span>
            <span className="text-xs font-mono text-muted-foreground">v2.4.1</span>
          </div>
          <div>
            <h3 className="font-semibold text-foreground text-base">
              SaaS Starter Kit
            </h3>
            <p className="text-xs text-muted-foreground mt-1">
              Full-stack template with billing, teams, and feature flags.
            </p>
          </div>
          <div className="flex items-center justify-between">
            <div className="flex gap-1">
              {["nextjs", "stripe", "auth"].map((tag) => (
                <span
                  key={tag}
                  className="px-2 py-0.5 rounded text-[10px] font-mono bg-secondary text-muted-foreground border border-border"
                >
                  {tag}
                </span>
              ))}
            </div>
            <span className="text-lg font-semibold text-foreground">$99</span>
          </div>
          <div className="h-px bg-border" />
          <div className="flex items-center gap-1.5">
            {[1, 2, 3, 4, 5].map((i) => (
              <svg key={i} className="w-3.5 h-3.5 text-yellow-400 fill-current" viewBox="0 0 20 20">
                <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
              </svg>
            ))}
            <span className="text-xs text-muted-foreground ml-1">4.9 (312)</span>
          </div>
        </div>
      </div>
      <div
        ref={badgeRef}
        className="absolute bottom-16 left-0 z-20 flex items-center gap-2 px-3 py-2 rounded-lg border border-border bg-card shadow-lg"
      >
        <div className="w-6 h-6 rounded-full bg-primary/20 flex items-center justify-center">
          <ShieldCheck className="w-3.5 h-3.5 text-primary" />
        </div>
        <div>
          <p className="text-xs font-medium text-foreground">Encrypted delivery</p>
          <p className="text-[10px] text-muted-foreground">End-to-end secured</p>
        </div>
      </div>
    </div>
  );
}
