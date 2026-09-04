import test from "node:test";
import assert from "node:assert/strict";
import { checkDiff, isApprovedSecretPath, parseUnifiedDiff } from "./pre-commit-guard.mjs";

const tsIgnoreDirective = "@ts-" + "ignore";
const eslintDisableDirective = "eslint-" + "disable";
const tsIgnoreRule = "no-ts-" + "ignore";
const eslintDisableRule = "no-eslint-" + "disable";
const syntheticStripeKey = "sk_" + "live_" + "51ABCDEF1234567890abcdefghijklmnopqrstuvwxyz";

test("parseUnifiedDiff parses git diff additions correctly", () => {
  const sampleDiff = `
diff --git a/asblock-backend/SomeService.cs b/asblock-backend/SomeService.cs
--- a/asblock-backend/SomeService.cs
+++ b/asblock-backend/SomeService.cs
@@ -10,0 +11,2 @@
+using Moq;
+public class Foo {}
`;
  const files = parseUnifiedDiff(sampleDiff);
  assert.equal(files.length, 1);
  assert.equal(files[0].file, "asblock-backend/SomeService.cs");
  assert.equal(files[0].addedLines.length, 2);
  assert.equal(files[0].addedLines[0].line, "using Moq;");
});

test("isApprovedSecretPath identifies test and fixture files", () => {
  assert.equal(isApprovedSecretPath("asblock-backend/AssetBlock.Application.Tests/SomeTest.cs"), true);
  assert.equal(isApprovedSecretPath("asblock-frontend/src/__tests__/auth.test.ts"), true);
  assert.equal(isApprovedSecretPath(".env.example"), true);
  assert.equal(isApprovedSecretPath("asblock-backend/AssetBlock.Application/UseCases/Login.cs"), false);
  assert.equal(isApprovedSecretPath("asblock-frontend/src/app/page.tsx"), false);
});

test("checkDiff blocks Moq in C# files", () => {
  const diff = `
diff --git a/asblock-backend/SomeTests.cs b/asblock-backend/SomeTests.cs
--- a/asblock-backend/SomeTests.cs
+++ b/asblock-backend/SomeTests.cs
@@ -1,0 +1,1 @@
+using Moq;
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === "no-moq"));
});

test(`checkDiff blocks ${tsIgnoreDirective} in TypeScript/JavaScript files`, () => {
  const diff = `
diff --git a/asblock-frontend/src/components/button.tsx b/asblock-frontend/src/components/button.tsx
--- a/asblock-frontend/src/components/button.tsx
+++ b/asblock-frontend/src/components/button.tsx
@@ -5,0 +6,1 @@
+// ${tsIgnoreDirective}: bypass type check
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === tsIgnoreRule));
});

test(`checkDiff blocks ${eslintDisableDirective} in source files`, () => {
  const diff = `
diff --git a/asblock-frontend/src/lib/api.ts b/asblock-frontend/src/lib/api.ts
--- a/asblock-frontend/src/lib/api.ts
+++ b/asblock-frontend/src/lib/api.ts
@@ -12,0 +13,1 @@
+/* ${eslintDisableDirective} @typescript-eslint/no-unused-vars */
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === eslintDisableRule));
});

test("checkDiff blocks explicit 'any' in production TypeScript files", () => {
  const diff = `
diff --git a/asblock-frontend/src/lib/api.ts b/asblock-frontend/src/lib/api.ts
--- a/asblock-frontend/src/lib/api.ts
+++ b/asblock-frontend/src/lib/api.ts
@@ -20,0 +21,1 @@
+const data: any = JSON.parse(raw);
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === "no-explicit-any"));
});

test("checkDiff allows clean code", () => {
  const diff = `
diff --git a/asblock-frontend/src/lib/api.ts b/asblock-frontend/src/lib/api.ts
--- a/asblock-frontend/src/lib/api.ts
+++ b/asblock-frontend/src/lib/api.ts
@@ -20,0 +21,2 @@
+const data: unknown = JSON.parse(raw);
+console.log("data", data);
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, true);
  assert.equal(res.violations.length, 0);
});

test("checkDiff blocks hardcoded live Stripe keys outside approved fixtures", () => {
  const diff = `
diff --git a/asblock-backend/Config.cs b/asblock-backend/Config.cs
--- a/asblock-backend/Config.cs
+++ b/asblock-backend/Config.cs
@@ -5,0 +6,1 @@
+var stripeKey = "${syntheticStripeKey}";
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === "no-secrets"));
});

test("checkDiff allows placeholder keys and approved test fixtures", () => {
  const diff = `
diff --git a/asblock-backend/AssetBlock.Application.Tests/TestData.cs b/asblock-backend/AssetBlock.Application.Tests/TestData.cs
--- a/asblock-backend/AssetBlock.Application.Tests/TestData.cs
+++ b/asblock-backend/AssetBlock.Application.Tests/TestData.cs
@@ -5,0 +6,1 @@
+var stripeKey = "${syntheticStripeKey}";
`;
  const res = checkDiff(diff);
  assert.equal(res.ok, true);
});

test("checkDiff flags unverified EF migrations without MIGRATION_VALIDATED", () => {
  const diff = `
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs
--- /dev/null
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs
@@ -0,0 +1,10 @@
+namespace AssetBlock.Infrastructure.Migrations;
+public partial class AddTest : Migration {}
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
--- a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
@@ -10,0 +11,1 @@
+// snapshot update
+`;
  const res = checkDiff(diff, { env: {} });
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === "ef-migration-unverified"));
});

test("checkDiff rejects ALLOW_MIGRATION and requires MIGRATION_VALIDATED=1", () => {
  const diff = `
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs
--- /dev/null
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs
@@ -0,0 +1,10 @@
+namespace AssetBlock.Infrastructure.Migrations;
+public partial class AddTest : Migration {}
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
--- a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
@@ -10,0 +11,1 @@
+// snapshot update
+`;
  const res = checkDiff(diff, { env: { ALLOW_MIGRATION: "1" } });
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === "ef-migration-unverified"));
});

test("checkDiff allows EF migrations when MIGRATION_VALIDATED=1", () => {
  const diff = `
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs
--- /dev/null
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/20260904120000_AddTest.cs
@@ -0,0 +1,10 @@
+namespace AssetBlock.Infrastructure.Migrations;
+public partial class AddTest : Migration {}
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
--- a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
@@ -10,0 +11,1 @@
+// snapshot update
+`;
  const res = checkDiff(diff, { env: { MIGRATION_VALIDATED: "1" } });
  assert.equal(res.ok, true);
});

test("checkDiff flags suspicious snapshot modification without migration file", () => {
  const diff = `
diff --git a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
--- a/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
+++ b/asblock-backend/AssetBlock.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs
@@ -10,0 +11,1 @@
+// manual snapshot edit
+`;
  const res = checkDiff(diff, { env: { MIGRATION_VALIDATED: "1" } });
  assert.equal(res.ok, false);
  assert.ok(res.violations.some((v) => v.rule === "ef-migration-mismatch"));
});
