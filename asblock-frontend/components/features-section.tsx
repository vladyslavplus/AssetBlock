import { Zap, GitFork, ShieldCheck, Layers, DollarSign, Users } from "lucide-react";

const FEATURES = [
  {
    icon: Layers,
    title: "Rich asset types",
    description:
      "From code packages and CLI tools to UI kits and database schemas — any digital artifact, properly categorized.",
  },
  {
    icon: ShieldCheck,
    title: "Verified licensing",
    description:
      "Every purchase includes a clear, developer-friendly license. Know exactly what you can build and ship.",
  },
  {
    icon: Zap,
    title: "Instant downloads",
    description:
      "Assets are delivered over encrypted channels the moment payment clears. No delays, no manual fulfillment.",
  },
  {
    icon: DollarSign,
    title: "Fair revenue split",
    description:
      "Sellers keep the majority. Transparent fee structure, no hidden charges, and payouts processed weekly.",
  },
  {
    icon: GitFork,
    title: "Version management",
    description:
      "Upload new versions of your assets and buyers get notified. Full changelog history keeps everyone in sync.",
  },
  {
    icon: Users,
    title: "Community ratings",
    description:
      "Verified-purchase reviews and star ratings help buyers make confident decisions without guesswork.",
  },
];

export function FeaturesSection() {
  return (
    <section className="py-20 sm:py-28" aria-labelledby="features-heading">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="text-center mb-14">
          <h2
            id="features-heading"
            className="text-3xl sm:text-4xl font-semibold text-foreground text-balance animate-fade-in"
          >
            Everything a developer marketplace needs
          </h2>
          <p className="mt-4 text-muted-foreground text-base sm:text-lg max-w-2xl mx-auto leading-relaxed animate-fade-in">
            Built for the way developers actually work — precise, fast, and trustworthy.
          </p>
        </div>

        <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-4">
          {FEATURES.map((feature) => {
            const Icon = feature.icon;
            return (
              <div
                key={feature.title}
                className="group rounded-xl border border-border p-6 flex flex-col gap-3 transition-smooth hover:border-primary/50 hover:bg-card hover:shadow-[0_8px_24px_rgba(124,58,237,0.15)] hover:translate-y-[-4px] focus-within:ring-2 focus-within:ring-primary focus-within:ring-offset-2 focus-within:ring-offset-background"
                style={{ background: "#11101A" }}
                role="region"
                aria-label={feature.title}
              >
                <div className="w-9 h-9 rounded-lg flex items-center justify-center border border-border bg-secondary transition-smooth group-hover:border-primary/60 group-hover:bg-primary/10">
                  <Icon className="w-4.5 h-4.5 text-muted-foreground group-hover:text-accent transition-smooth" size={18} />
                </div>
                <h3 className="font-semibold text-foreground text-sm">{feature.title}</h3>
                <p className="text-sm text-muted-foreground leading-relaxed">{feature.description}</p>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}
