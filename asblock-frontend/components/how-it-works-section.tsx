import { Upload, CreditCard, Download } from "lucide-react";

const STEPS = [
  {
    number: "01",
    icon: Upload,
    title: "Upload your asset",
    description:
      "Package your code, templates, or tools. Add metadata, versioning, tags, and set your price. Takes minutes.",
  },
  {
    number: "02",
    icon: CreditCard,
    title: "Buyer completes checkout",
    description:
      "Customers browse, pick what they need, and purchase through our secure checkout. You get notified instantly.",
  },
  {
    number: "03",
    icon: Download,
    title: "Encrypted download delivered",
    description:
      "Files are delivered to the buyer over an encrypted channel immediately after payment clears. Zero manual steps.",
  },
];

export function HowItWorksSection() {
  return (
    <section
      className="py-20 sm:py-28 border-t border-border"
      aria-labelledby="how-it-works-heading"
    >
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="text-center mb-14">
          <h2
            id="how-it-works-heading"
            className="text-3xl sm:text-4xl font-semibold text-foreground text-balance animate-fade-in"
          >
            How it works
          </h2>
          <p className="mt-4 text-muted-foreground text-base sm:text-lg max-w-xl mx-auto leading-relaxed animate-fade-in">
            From upload to delivery in three straightforward steps.
          </p>
        </div>

        <div className="grid sm:grid-cols-3 gap-4 relative">
          <div
            className="hidden sm:block absolute top-10 left-1/6 right-1/6 h-px bg-gradient-to-r from-transparent via-border to-transparent"
            aria-hidden="true"
          />

          {STEPS.map((step) => {
            const Icon = step.icon;
            return (
              <div
                key={step.number}
                className="relative rounded-xl border border-border p-6 flex flex-col gap-4 transition-smooth hover:border-primary/50 hover:bg-card-elevated hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)]"
                style={{ background: "#11101A" }}
              >
                <div className="flex items-center gap-3">
                  <div className="w-10 h-10 rounded-lg flex items-center justify-center border border-border bg-secondary transition-smooth group-hover:border-primary/60 group-hover:bg-primary/10 shrink-0">
                    <Icon className="w-5 h-5 text-muted-foreground" />
                  </div>
                  <span className="font-mono text-xs text-muted-foreground/60 tracking-widest">
                    {step.number}
                  </span>
                </div>
                <h3 className="font-semibold text-foreground text-sm">{step.title}</h3>
                <p className="text-sm text-muted-foreground leading-relaxed">{step.description}</p>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}
