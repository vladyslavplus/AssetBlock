# Backend Review Lane

Read `asblock-backend/AGENTS.md` first. This reference adds review prompts; the nested guide remains canonical.

## Correctness and contracts

- Trace changed controller, command/query, validator, handler, store, entity/configuration, and tests as one behavior.
- Verify HTTP status, error code/message, serialization, pagination, cancellation, and authorization stay consistent with callers and frontend contracts.
- Check business decisions occur in Application/domain-facing abstractions rather than controllers or persistence plumbing.
- Flag missing validation only when untrusted input can reach a changed behavior incorrectly.

## Persistence, transactions, and concurrency

- Check read/write races against database-enforced invariants: unique indexes, conditional updates, row locks, and idempotency.
- Verify transactions stay short and exclude Stripe, MinIO, Redis, SignalR, SMTP, encryption streams, and other slow I/O.
- Inspect PostgreSQL exception translation for exact expected constraints; unexpected database failures must remain visible.
- For reads, check projection, `AsNoTracking`, deterministic paging, database-side filtering, and accidental N+1/entity-graph loading.
- Require index/query changes only when the changed query shape and expected cardinality justify them.

## Commerce and entitlements

- Stripe webhook verification remains the sole payment-completion authority; redirects must not create orders or purchases.
- Validate checkout intent ownership, amount, currency, session metadata, reservations, retries, and duplicate webhook delivery.
- Preserve atomic `Order -> OrderLine -> Purchase` fulfillment and per-asset entitlement/version rules for direct and bundle checkout.
- Check soft/hard delete decisions against purchases and active checkouts; existing buyers must retain authorized downloads.

## Security and privacy

- Verify role, verified-email, ownership, and download authorization at server boundaries without client-trusted shortcuts.
- Treat tokens, passwords, action links, encryption keys, Stripe payloads, plaintext assets, storage credentials, and full request bodies as prohibited log/audit/output data.
- Review new audit metadata as an explicit allowlist. Email, IP, User-Agent, visitor/session identifiers, and free text require a concrete purpose and bounded exposure.
- Preserve AES-GCM chunk nonce/AAD/order/end-of-stream checks and server-built MinIO object keys.
- For analytics ingestion, verify BFF trust/signature boundaries, retention rules, DNT/GPC behavior, rate-limit failure policy, and separation from commerce truth.

## Side effects and resilience

- Side effects that must survive process failure belong in the established outbox flow and remain idempotent under at-least-once delivery.
- Cache invalidation occurs only after committed writes; cache degradation must not hide authoritative failures.
- Retries must be limited to safe/idempotent operations and must not multiply payments, emails, notifications, or storage mutations.
- Logs should contain stable safe identifiers and useful exception context without sensitive payloads.

## Verification

- Demand focused handler/validator tests for business behavior and PostgreSQL integration tests for constraints, transactions, locks, search, or provider-specific SQL.
- Use WebApi integration tests for routing, model binding, auth/policies, middleware, rate limits, DI, and error contracts.
- Missing tests are findings only when changed behavior lacks proportionate regression protection; specify the exact scenario to add.
