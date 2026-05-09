---
name: assetblock-frontend
description: >-
  Frontend conventions for the AssetBlock Next.js app: App Router structure,
  BFF auth/session flows, shared API helpers, forms with Zod and react-hook-form,
  and the shadcn/ui plus sonner stack. Use when editing, reviewing, or adding
  features in asblock-frontend.
---

# AssetBlock frontend conventions

## App structure

- This frontend uses **Next.js App Router**.
- Route handlers live under `app/api/`.
- Shared browser/server helpers live under `lib/` and `lib/server/`.
- Prefer extending the existing shape of `app/`, `components/`, and `lib/` instead of inventing new top-level patterns.

## Auth and API access

- Treat auth as a **BFF-style flow**: the frontend talks to its own `app/api/*` routes, and those routes handle cookies plus backend calls.
- Keep auth tokens in **server-managed httpOnly cookies** only.
- Reuse existing helpers before creating new fetch layers:
  - `lib/api-client.ts`
  - `lib/api-config.ts`
  - `lib/api-errors.ts`
  - `lib/server/auth-cookies.ts`
  - `lib/server/refresh-session.ts`
  - `lib/server/backend-authorized.ts`
- For session-aware backend calls, prefer the existing refresh-on-401 pattern instead of duplicating token rotation logic.

## Forms and validation

- Use **Zod** for payload schemas and **react-hook-form** with `zodResolver` for forms.
- Keep schemas in shared `lib/*-schemas.ts` or feature-local modules when that matches nearby code.
- Show validation errors inline first; use toast feedback only for broader action outcomes.

## UI stack

- Follow the existing stack: **shadcn/ui**, **Radix**, **Tailwind CSS**, **lucide-react**, and **sonner**.
- Reuse existing UI primitives from `components/ui/` before adding one-off markup.
- Match the established typography, spacing, and token usage in nearby files.

## Error handling

- In route handlers, return clear `NextResponse.json(...)` error bodies with appropriate status codes.
- For auth/session endpoints, preserve the existing **fail-soft** behavior where anonymous UI is better than a 500.
- On the client, prefer existing error helpers like `getMessageFromApiErrorBody(...)`.

## Change discipline

- Keep fetch and contract changes explicit when a frontend change depends on backend behavior.
- Avoid introducing new state or data-fetching libraries unless the repo already standardizes on them.
- When changing auth, session, or cookie behavior, check both the route handler and the corresponding server helper.

## Related

- Backend conventions: [../assetblock-backend/SKILL.md](../assetblock-backend/SKILL.md)
