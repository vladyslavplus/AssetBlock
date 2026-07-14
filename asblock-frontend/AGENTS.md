# AssetBlock Frontend Agent Guide

## Project and architecture

- This is the canonical guide for `asblock-frontend/`: Next.js 16 App Router, React 19, strict TypeScript, Tailwind CSS v4, shadcn/Radix primitives, TanStack Query v5, React Hook Form, Zod, Sonner, SignalR, and pnpm.
- Preserve the existing layout: routes and Route Handlers in `app/`, shared UI in `components/`, feature API/types/query modules in `lib/`, server-only helpers in `lib/server/`, and reusable hooks in `hooks/`.
- Read nearby routes, components, schemas, hooks, and server helpers before changing a feature. Keep diffs narrow; do not redesign unrelated UI or revert user work.
- Do not introduce Redux, a second query library, CSS modules, another component library, or a parallel auth/fetch abstraction without explicit approval.
- Reuse `components/ui` primitives and existing feature patterns. Do not refactor generated shadcn-style primitives unless they contain AssetBlock-specific behavior.

## Rendering and component boundaries

- Prefer Server Components for server-side reads. Keep Client Component boundaries small and add `use client` only for browser APIs, event handlers, React Query, forms, or interactive state.
- Never fetch remote data in `useEffect`. Use server helpers for server reads and TanStack Query hooks for client-cached BFF/API data.
- Keep local state near the markup that owns it. Use Context only for genuine cross-tree state; do not create global state for feature-local concerns.
- Avoid large JSX/stateful components. Split a feature into schema/types, data hook, stateful container, and presentational subcomponents only when it reduces real complexity.
- Use loading, error, empty, pending, and disabled states for async user flows. Do not present failed operations as success or hide unavailable data.
- Preserve metadata, semantic HTML, keyboard navigation, accessible labels, focus management, and responsive behavior when editing pages/components.

## Data flow and TanStack Query

- Public catalog/detail reads may use the existing browser API helpers. Authenticated browser operations must use the Next.js BFF routes under `app/api`.
- Reuse `apiFetch`, API config/error helpers, server auth helpers, and feature fetchers before creating a new wrapper.
- Keep query keys next to their feature fetchers in existing `*-query.ts` modules. Invalidate affected keys after mutations or SignalR events.
- Use `setQueryData` only for small, safe optimistic patches; otherwise invalidate and refetch. Clear user-specific cache on logout/session loss.
- Keep list filters and pagination deterministic and URL-compatible. Debounce high-frequency user input only when it prevents unnecessary requests without harming accessibility.
- Avoid request waterfalls: start independent server reads together, fetch as close to the consumer as possible, and do not fetch the same data in both server and client without a clear hydration need.
- Do not introduce Next.js cache/revalidation behavior or a new caching layer unless the task explicitly needs it; TanStack Query is the established client cache.

## BFF, auth, and security

- The authenticated flow is BFF-style: browser -> `app/api/*` -> backend. Keep access and refresh tokens only in server-managed `httpOnly` cookies.
- Never place tokens in localStorage, sessionStorage, query strings, logs, client-visible environment variables, or UI error messages.
- Reuse `lib/server/auth-cookies`, `refresh-session`, and `backend-authorized`; do not duplicate token refresh, cookie writes, or Authorization header construction.
- Validate Route Handler payloads with existing Zod schemas before forwarding. Treat form values, URL parameters, browser state, and backend error bodies as untrusted.
- Preserve same-origin/CSRF protection for mutating BFF routes. Do not weaken cookie flags, CORS, redirect validation, role checks, or backend authorization.
- API authorization is authoritative. Page/route guards are UX only and cannot be the sole access-control mechanism.
- Never use untrusted HTML, arbitrary script injection, or secret values in `NEXT_PUBLIC_*` variables.

## Forms, API contracts, and errors

- Use Zod plus React Hook Form with `zodResolver` for interactive forms. Keep schemas near feature types/API modules and use `useWatch`, not `form.watch()`.
- Show field validation errors inline; use Sonner for broader mutation outcomes. Reuse existing error parsing helpers and `ApiRequestError`.
- Keep frontend/backend contract changes explicit. When backend payload, error contract, status handling, or auth behavior changes, update BFF handlers, feature API modules, query hooks, UI feedback, and tests in the same change.
- Preserve backend error details safely: never render arbitrary backend HTML or expose internal stack traces. Provide a useful fallback message for unknown failures.
- Route Handlers should preserve required status and safe response headers. Downloads must preserve `Content-Type` and `Content-Disposition` without forwarding unsafe headers blindly.

## UI, styling, and performance

- Follow the existing dark-theme visual system, typography, spacing, design tokens, Tailwind conventions, shadcn/Radix primitives, Lucide icons, and Sonner feedback patterns.
- Keep Tailwind utilities inline in `className` or `cn(...)`. Do not hoist class strings into module-level styling constants; use a small presentational component if reuse is meaningful.
- Prefer CSS/Tailwind and simple React composition over unnecessary JavaScript-driven layout or animation. Respect `prefers-reduced-motion` where motion is added.
- Avoid shipping heavy client code to routes that can stay server-rendered. Lazy-load genuinely heavy, below-the-fold, or browser-only visual features when it materially improves initial load.
- Keep images, lists, filters, and animations performant: avoid rendering unbounded lists, expensive work during render, unstable keys, unnecessary state duplication, and recreating objects/functions that trigger avoidable child work.
- Do not alter `images.unoptimized`, theme forcing, analytics, or global styling without an explicit product/deployment reason.

## TypeScript and style

- Preserve strict TypeScript. Do not use `any`, broad casts, disabled lint rules, `@ts-ignore`, or non-null assertions to hide a genuine type/design problem.
- Use functional components, descriptive names, early returns, small focused helpers, and concise JSDoc for public/non-obvious utilities.
- Keep code and contracts English. Avoid magic strings when an existing constant, type, schema, or route helper fits.
- Do not rename routes, environment keys, query keys, exported types, or feature folders unless required by the task.

## Tests and verification

- Add focused tests for behavior changes: happy path, validation/error behavior, auth/session behavior, mutation invalidation, and regression-prone edge cases.
- Don't Run `pnpm run lint` after code changes - only if really needed; run `pnpm run check` for formatting; run `pnpm run build` when routing, server rendering, configuration, or TypeScript boundaries change.
- Keep new tests feature-local and follow the repository's chosen test tooling. Do not add a second test framework without approval.
- If verification cannot run, state precisely what was not verified and why.
