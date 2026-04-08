export function FinalCtaSection() {
  return (
    <section
      className="py-20 sm:py-28"
      aria-labelledby="final-cta-heading"
    >
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div
          className="relative overflow-hidden rounded-2xl border border-border p-10 sm:p-16 text-center flex flex-col items-center gap-6"
          style={{ background: "#0F0E1C" }}
        >
          <div
            className="pointer-events-none absolute inset-0 opacity-[0.12]"
            style={{
              background:
                "radial-gradient(ellipse at 50% 120%, #7C3AED 0%, transparent 65%)",
            }}
            aria-hidden="true"
          />

          <div className="relative flex flex-col items-center gap-4 animate-fade-in">
            <span className="text-xs font-mono text-accent tracking-widest uppercase px-3 py-1 rounded-full border border-accent/30 bg-accent/5">
              Start selling today
            </span>
            <h2
              id="final-cta-heading"
              className="text-3xl sm:text-4xl lg:text-5xl font-semibold text-foreground text-balance max-w-2xl"
              style={{ fontFamily: "var(--font-space-grotesk)" }}
            >
              Your code is worth more than a GitHub star
            </h2>
            <p className="text-muted-foreground text-base sm:text-lg max-w-lg leading-relaxed">
              Join thousands of developers monetizing their IP. List your first
              asset free, no subscription required.
            </p>
          </div>

          <div className="relative flex flex-wrap items-center justify-center gap-3">
            <button
              className="px-6 py-3 rounded-lg bg-primary text-primary-foreground font-medium shadow-[0_0_28px_rgba(124,58,237,0.35)] hover:shadow-[0_0_36px_rgba(124,58,237,0.45)] hover:bg-[#6D28D9] transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            >
              Create an account
            </button>
            <button
              className="px-6 py-3 rounded-lg border border-border text-foreground bg-transparent hover:bg-secondary/50 hover:border-foreground/40 hover:text-foreground font-medium transition-smooth focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            >
              Browse catalog
            </button>
          </div>
        </div>
      </div>
    </section>
  );
}
