"use client";

import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { ShieldCheck, Lock, Code2 } from "lucide-react";

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

export function HeroSection() {
  const heroRef = useRef<HTMLDivElement>(null);
  const glowRef = useRef<HTMLDivElement>(null);
  const [mousePos, setMousePos] = useState({ x: 0, y: 0 });
  const prefersReducedMotion = useSyncExternalStore(
    subscribeReducedMotion,
    getReducedMotionSnapshot,
    getReducedMotionServerSnapshot,
  );

  useEffect(() => {
    if (prefersReducedMotion) {
      return;
    }

    const heroEl = heroRef.current;
    const glowEl = glowRef.current;
    if (!heroEl || !glowEl) {
      return;
    }

    let rafId: number;
    let targetX = 0;
    let targetY = 0;

    const handleMouseMove = (e: MouseEvent) => {
      const rect = heroEl.getBoundingClientRect();
      targetX = e.clientX - rect.left;
      targetY = e.clientY - rect.top;
    };

    const updateGlow = () => {
      setMousePos((prev) => ({
        x: prev.x + (targetX - prev.x) * 0.15,
        y: prev.y + (targetY - prev.y) * 0.15,
      }));
      rafId = requestAnimationFrame(updateGlow);
    };

    heroEl.addEventListener("mousemove", handleMouseMove);
    rafId = requestAnimationFrame(updateGlow);

    return () => {
      heroEl.removeEventListener("mousemove", handleMouseMove);
      cancelAnimationFrame(rafId);
    };
  }, [prefersReducedMotion]);

  useEffect(() => {
    if (!glowRef.current) return;
    glowRef.current.style.transform = `translate(${mousePos.x}px, ${mousePos.y}px)`;
  }, [mousePos]);

  return (
    <section
      ref={heroRef}
      className="relative overflow-hidden pt-32 pb-20 sm:pt-40 sm:pb-28"
    >
      <div
        className="pointer-events-none absolute inset-0 opacity-[0.035]"
        style={{
          backgroundImage:
            "linear-gradient(to right, #9A96B0 1px, transparent 1px), linear-gradient(to bottom, #9A96B0 1px, transparent 1px)",
          backgroundSize: "40px 40px",
        }}
        aria-hidden="true"
      />

      <div
        className="pointer-events-none absolute -top-32 left-1/2 -translate-x-1/2 w-[800px] h-[600px] opacity-15"
        style={{
          background:
            "radial-gradient(ellipse at center, #7C3AED 0%, transparent 70%)",
        }}
        aria-hidden="true"
      />

      <div
        ref={glowRef}
        className="pointer-events-none absolute w-[600px] h-[400px] opacity-0 transition-opacity"
        style={{
          background:
            "radial-gradient(ellipse at center, #7C3AED 0%, transparent 60%)",
          transform: "translate(-50%, -50%)",
          willChange: "transform",
        }}
        aria-hidden="true"
      />

      <div className="relative max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="grid lg:grid-cols-2 gap-12 items-center">
          <div className="flex flex-col gap-6 animate-fade-in">
            <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full border border-accent/30 bg-accent/5 w-fit">
              <span className="text-[11px] font-mono text-accent tracking-wider uppercase">
                v1.0 — Now open for sellers
              </span>
            </div>

            <h1
              className="text-4xl sm:text-5xl lg:text-6xl font-semibold leading-[1.1] text-balance text-foreground"
              style={{ fontFamily: "var(--font-space-grotesk)" }}
            >
              The marketplace for{" "}
              <span className="text-primary">developer IP</span>
            </h1>

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
                <Link href="#">Become a seller</Link>
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

          <div className="relative hidden lg:block animate-fade-in-delay" aria-hidden="true">
            <HeroIllustration />
          </div>
        </div>
      </div>
    </section>
  );
}

function HeroIllustration() {
  return (
    <div className="relative flex items-center justify-center h-[440px]">
      <div
        className="absolute right-0 top-8 w-72 h-48 rounded-xl border border-border bg-card-elevated rotate-3 opacity-60"
        style={{ background: "#141322" }}
      />
      <div
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
      {/* Floating badge */}
      <div className="absolute bottom-16 left-0 z-20 flex items-center gap-2 px-3 py-2 rounded-lg border border-border bg-card shadow-lg">
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
