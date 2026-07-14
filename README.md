# AssetBlock (AsBlock)

Monorepo for **AssetBlock**, a developer-oriented marketplace for digital intellectual property: code packages, templates, tools, and similar assets. Sellers list encrypted deliverables; buyers browse a catalog, pay through integrated checkout, and access purchases from a personal library.

This repository is a non-commercial / academic project.

## Repository layout

```
asblock/
├── asblock-backend/           # ASP.NET Core Web API + application/domain/infrastructure
│   ├── asblock-backend.sln
│   ├── docker-compose.yml     # Local dependencies (e.g. PostgreSQL, Redis, Elasticsearch, MinIO)
│   └── README.md
└── asblock-frontend/          # Next.js web application (App Router)
```

## Backend (`asblock-backend`)

**Stack:** .NET 10, ASP.NET Core Web API, **Clean Architecture** (Domain / Application / Infrastructure / WebApi), CQRS-style use cases with **MediatR**, **FluentValidation**, **Ardalis.Result**, Entity Framework Core with PostgreSQL, optional **Redis** caching, **Elasticsearch** for catalog search indexing, **MinIO** (S3-compatible) for encrypted asset storage, **Stripe** Checkout and webhooks, **SignalR** for real-time notifications, **Serilog** for structured logging.

**High-level capabilities:** JWT-based auth (access + refresh), role-based access (including admin), asset lifecycle (upload, update, tags, download for purchasers or author), soft delete when purchases exist (delist from catalog while keeping DB row and blob for buyers), hard delete when there are no purchases, categories and tags (admin writes), reviews, user profiles and social links, notifications, Stripe-backed purchases and library.

**Typical commands** (from `asblock-backend/`):

```bash
dotnet restore
dotnet build
dotnet test asblock-backend.sln
dotnet ef database update --project AssetBlock.Infrastructure --startup-project AssetBlock.WebApi
dotnet run --project AssetBlock.WebApi
```

Bring up dependencies with Docker Compose when needed (`docker-compose.yml` in the backend folder). Configure API secrets with **.NET User Secrets** (see `asblock-backend/README.md`). Tracked `appsettings*.json` and `.env.example` files must not contain real secrets.

## Frontend (`asblock-frontend`)

**Stack:** **Next.js** (App Router), **React**, **TypeScript**, **Tailwind CSS**, **TanStack Query** for server state, **react-hook-form** with **Zod** for validation, **next-themes**, **Radix UI**-style primitives under `components/ui`, **lucide-react** icons, **@microsoft/signalr** for notifications. Route Handlers under `app/api/` act as a **BFF**: proxy to the backend and use **httpOnly** cookies for session tokens where applicable.

**High-level capabilities:** marketing home with featured catalog strip, asset catalog with filters and pagination, asset detail and checkout flow, authenticated library and account settings, seller hub (listings, upload, edit), admin UI for categories/tags/review moderation, lightweight docs page, login and registration.

**Typical commands** (from `asblock-frontend/`):

```bash
pnpm install
pnpm dev
pnpm run build
pnpm run lint
```

Point the frontend at your running Web API using environment variables (see `asblock-frontend/.env.example`): **`NEXT_PUBLIC_API_BASE_URL`** for browser-side requests, and **`ASSETBLOCK_API_BASE_URL`** (or the public URL as fallback) for server-side Route Handlers.

## Documentation

- **Interactive API:** Swagger UI when the Web API is running in Development.

## Contributing / quality

Run backend tests and frontend lint (and build) before submitting changes. Keep secrets out of source control.
