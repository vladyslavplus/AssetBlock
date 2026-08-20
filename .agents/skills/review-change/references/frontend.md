# Frontend Review Lane

Read `asblock-frontend/AGENTS.md` first. This reference adds review prompts; the nested guide remains canonical.

## Boundaries and contracts

- Trace page/component, schema/types, API helper, query/mutation hook, BFF Route Handler, and backend contract together.
- Verify status codes, safe error bodies, redirects, download headers, optional fields, enums, paging, and currency/date semantics remain aligned.
- Server Components should own suitable server reads; Client Components need a real browser, interaction, form, or React Query reason.
- Flag duplicated fetch/auth abstractions and remote-data `useEffect` flows when existing server or TanStack Query patterns should handle them.

## Authentication and security

- Authenticated browser operations go through same-origin BFF routes; access/refresh tokens remain server-only in `httpOnly` cookies.
- Check cookie flags, CSRF/origin validation, refresh behavior, role/route UX guards, and backend authorization without treating client guards as security.
- Reject tokens, credentials, internal URLs, signing secrets, or sensitive identifiers in `NEXT_PUBLIC_*`, storage, query strings, logs, analytics payloads, or rendered errors.
- Treat route params, form values, backend error bodies, filenames, redirect targets, and HTML as untrusted.
- Preserve safe `Content-Type`/`Content-Disposition` forwarding for downloads without proxying arbitrary headers.

## Privacy and analytics

- Optional telemetry must respect DNT/GPC and avoid creating analytics cookies/events when opted out.
- Check visitor/session cookies, referrer/source data, IP-derived context, and event properties for minimization, bounded retention, and no account/content secrets.
- Commerce and entitlements must come from backend orders/purchases, never client telemetry.
- Avoid exposing user-specific query data across logout, session loss, account switches, SSR caches, or shared browser state.

## State and failure behavior

- Query keys must include every input that changes results; mutations and SignalR events must invalidate or update all affected views.
- Clear user-scoped cache on logout/session loss. Optimistic updates need a safe rollback or invalidation path.
- Check loading, empty, error, pending, disabled, retry, and partial-data states. Failed operations must not look successful.
- Forms must keep Zod and backend constraints aligned and surface field versus global errors appropriately.

## Performance and accessibility

- Look for request waterfalls, duplicate server/client fetches, accidental dynamic rendering, oversized client boundaries, unbounded lists, unstable keys, and heavy browser-only dependencies.
- Do not request manual memoization without measured need; React Compiler is enabled.
- Performance findings need a realistic affected route and mechanism, not generic advice.
- Preserve semantic HTML, keyboard behavior, labels, focus management, responsive layout, reduced motion, and meaningful loading announcements.

## Verification

- Require focused tests only where existing frontend tooling supports them and changed behavior has meaningful regression risk.
- `pnpm run check` covers typecheck, lint, and formatting. Add `pnpm run build` for routing, Server Components, configuration, metadata, or TypeScript boundary changes.
- For browser-sensitive behavior, state the exact flow that needs manual or browser verification.
