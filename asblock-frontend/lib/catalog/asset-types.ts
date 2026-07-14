export interface AssetListItem {
  id: string;
  title: string;
  description: string | null;
  price: number;
  categoryId: string;
  categoryName: string | null;
  authorId: string;
  authorUsername: string;
  createdAt: string;
  tags: string[];
  averageRating: number;
}

export const FEATURED_ASSETS_MOCK: AssetListItem[] = [
  {
    id: "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    title: "React Dashboard Boilerplate",
    description: "Production-ready admin dashboard with auth, charts, and data tables. Fully typed with TypeScript.",
    price: 49,
    categoryId: "cat-001",
    categoryName: "Templates",
    authorId: "usr-001",
    authorUsername: "devcraft",
    createdAt: "2025-11-14T09:22:00Z",
    tags: ["react", "dashboard", "boilerplate", "typescript"],
    averageRating: 4.8,
  },
  {
    id: "b2c3d4e5-f6a7-8901-bcde-f12345678901",
    title: "Next.js SaaS Starter Kit",
    description: "Full-stack SaaS template with billing, team management, and feature flags pre-wired.",
    price: 99,
    categoryId: "cat-002",
    categoryName: "Starter Kits",
    authorId: "usr-002",
    authorUsername: "saasforge",
    createdAt: "2025-12-01T14:05:00Z",
    tags: ["nextjs", "saas", "billing", "stripe"],
    averageRating: 4.9,
  },
  {
    id: "c3d4e5f6-a7b8-9012-cdef-123456789012",
    title: "CLI Scaffolding Tool",
    description: "Node.js CLI for generating project structures with customizable templates and prompts.",
    price: 19,
    categoryId: "cat-003",
    categoryName: "CLI Tools",
    authorId: "usr-003",
    authorUsername: "terminaldev",
    createdAt: "2025-10-08T07:44:00Z",
    tags: ["cli", "nodejs", "scaffold"],
    averageRating: 4.5,
  },
  {
    id: "d4e5f6a7-b8c9-0123-defa-234567890123",
    title: "Prisma Schema Pack",
    description: null,
    price: 14,
    categoryId: "cat-004",
    categoryName: "Database",
    authorId: "usr-004",
    authorUsername: "ormwizard",
    createdAt: "2026-01-22T11:30:00Z",
    tags: ["prisma", "database", "schema"],
    averageRating: 4.3,
  },
  {
    id: "e5f6a7b8-c9d0-1234-efab-345678901234",
    title: "Tailwind UI Component Library",
    description: "60+ accessible, dark-mode-ready components built with Tailwind CSS and Radix primitives.",
    price: 79,
    categoryId: "cat-001",
    categoryName: "Templates",
    authorId: "usr-005",
    authorUsername: "pixelstack",
    createdAt: "2026-02-10T16:55:00Z",
    tags: ["tailwind", "ui", "radix", "accessible"],
    averageRating: 4.7,
  },
  {
    id: "f6a7b8c9-d0e1-2345-fabc-456789012345",
    title: "GitHub Actions Workflow Pack",
    description: "Battle-tested CI/CD workflow templates for Node.js, Docker, and multi-cloud deployments.",
    price: 29,
    categoryId: "cat-005",
    categoryName: "DevOps",
    authorId: "usr-006",
    authorUsername: "pipelineops",
    createdAt: "2026-03-03T08:12:00Z",
    tags: ["github-actions", "cicd", "devops"],
    averageRating: 4.6,
  },
];
