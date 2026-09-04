---
name: add-migration
description: Safely generate, assess, inspect, and verify Entity Framework Core database migrations in AssetBlock. Use whenever a schema change is required; never generate or hand-edit migrations without explicit approval.
---

# Add Migration

## 1. Approval and Assessment

1. **Verify user approval**: Never create or modify database schema without explicit prior approval from the user.
2. **Assess impact**:
   - **Destructive changes**: Check for dropped columns, dropped tables, or narrowed column types that cause data loss.
   - **Backfills & nullability**: For new non-nullable columns on existing populated tables, specify a sensible default or backfill strategy.
   - **Indexing**: Evaluate whether new foreign keys or high-cardinality search fields need indexes (e.g., PostgreSQL `CONCURRENTLY` if large-scale production, or standard EF indexes for development).

## 2. Generate via dotnet ef CLI

Generate migrations exclusively using the official CLI; never author migration classes or snapshot files by hand:

```bash
dotnet ef migrations add <MigrationName> \
  --project asblock-backend/AssetBlock.Infrastructure/AssetBlock.Infrastructure.csproj \
  --startup-project asblock-backend/AssetBlock.WebApi/AssetBlock.WebApi.csproj \
  --output-dir Migrations
```

## 3. Inspect Generated Artifacts

- Inspect `asblock-backend/AssetBlock.Infrastructure/Migrations/<Timestamp>_<MigrationName>.cs`, `<Timestamp>_<MigrationName>.Designer.cs`, and `asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs`.
- Verify the `Up` method matches intended schema changes and contains no unintended table drops or column alterations.
- Verify the `Down` method accurately reverses all operations performed in `Up`.
- **Do not hand-edit** the generated migration or snapshot code. If changes are incorrect, remove the migration with `dotnet ef migrations remove --project asblock-backend/AssetBlock.Infrastructure/AssetBlock.Infrastructure.csproj --startup-project asblock-backend/AssetBlock.WebApi/AssetBlock.WebApi.csproj` and adjust entity/configuration mappings instead.

## 4. Smoke Verification

- Execute verification against the database:
  - Run database migration service or integration tests (e.g. `dotnet test asblock-backend/AssetBlock.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Postgres"`).
  - Verify application startup succeeds and health probes report healthy.

## 5. Pre-Commit Guardrail and Delivery

- Staged migrations are guarded by `scripts/git/pre-commit-guard.mjs`.
- Once verified, stage the migration files and model snapshot together.
- Run the commit with `MIGRATION_VALIDATED=1`:
  - **PowerShell (Windows)**:
    ```powershell
    $env:MIGRATION_VALIDATED="1"; git commit -m "..."; Remove-Item Env:\MIGRATION_VALIDATED
    ```
  - **Bash / POSIX**:
    ```bash
    MIGRATION_VALIDATED=1 git commit -m "..."
    ```
- Provide a concise report including:
  - Migration name and generated files.
  - Verification commands executed, exit codes, and test outcomes.
  - Confirmation of non-destructive schema impact.
