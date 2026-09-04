import { execSync } from "node:child_process";

/**
 * Parses unified diff text into an array of file diff objects.
 * Each file contains { file: string, addedLines: Array<{ line: string, lineNum?: number }> }.
 */
export function parseUnifiedDiff(diffText) {
  const files = [];
  const lines = diffText.split(/\r?\n/);
  let currentFile = null;
  let inHunk = false;

  for (const line of lines) {
    if (line.startsWith("diff --git ")) {
      const parts = line.split(" ");
      const bPath = parts[parts.length - 1];
      const filePath = bPath.replace(/^[a-b]\//, "");
      currentFile = { file: filePath, addedLines: [] };
      files.push(currentFile);
      inHunk = false;
      continue;
    }

    if (line.startsWith("+++ ")) {
      inHunk = true;
      continue;
    }

    if (line.startsWith("--- ")) {
      continue;
    }

    if (inHunk && line.startsWith("+") && !line.startsWith("+++")) {
      const addedContent = line.slice(1);
      if (currentFile) {
        currentFile.addedLines.push({ line: addedContent });
      }
    }
  }

  return files;
}

const APPROVED_SECRET_PATH_PATTERNS = [
  /[\\/]test[\\/]/i,
  /[\\/]tests[\\/]/i,
  /[\\/]__tests__[\\/]/i,
  /[\\/]fixtures[\\/]/i,
  /[\\/]examples[\\/]/i,
  /[\\/]mocks[\\/]/i,
  /\.test\.[a-z0-9]+$/i,
  /\.spec\.[a-z0-9]+$/i,
  /Test\.cs$/i,
  /Tests\.cs$/i,
  /TestData\.cs$/i,
  /TestBase\.cs$/i,
  /\.env\.example$/i,
  /\.env\.test$/i,
];

const HIGH_CONFIDENCE_SECRET_PATTERNS = [
  /\b(?:sk_live|sk_test|rk_live|rk_test)_[0-9a-zA-Z]{20,}\b/,
  /\bghp_[0-9a-zA-Z]{30,}\b/,
  /\bAKIA[0-9A-Z]{16}\b/,
  /(?:api[_-]?key|jwt[_-]?secret|private[_-]?key|secret[_-]?key)\s*[:=]\s*["'][A-Za-z0-9_-]{24,}["']/i,
];

const PLACEHOLDER_STRINGS = [
  "change_me",
  "changeme",
  "placeholder",
  "your-secret",
  "dummy",
  "example",
  "test_secret",
  "mock",
  "localhost",
  "password123",
];

// Keep guard source free of directives it rejects in staged JavaScript files.
const TS_IGNORE_DIRECTIVE = "@ts-" + "ignore";
const ESLINT_DISABLE_DIRECTIVE = "eslint-" + "disable";
const NO_TS_IGNORE_RULE = "no-ts-" + "ignore";
const NO_ESLINT_DISABLE_RULE = "no-eslint-" + "disable";
const TS_IGNORE_PATTERN = new RegExp(`${TS_IGNORE_DIRECTIVE}\\b`);
const ESLINT_DISABLE_PATTERN = new RegExp(`${ESLINT_DISABLE_DIRECTIVE}\\b`);

export function isApprovedSecretPath(filePath) {
  const normalized = filePath.replace(/\\/g, "/");
  return APPROVED_SECRET_PATH_PATTERNS.some((p) => p.test(normalized));
}

/**
 * Inspects parsed files against guard rules.
 * @param {Array<{ file: string, addedLines: Array<{ line: string }> }>} files
 * @param {{ env?: Record<string, string | undefined> }} [options]
 */
export function validateFiles(files, options = {}) {
  const env = options.env || process.env;
  const violations = [];

  let hasMigrationFile = false;
  let hasSnapshotFile = false;
  const migrationFiles = [];

  for (const { file, addedLines } of files) {
    const normFile = file.replace(/\\/g, "/");
    const isCs = normFile.endsWith(".cs");
    const isTs = normFile.endsWith(".ts") || normFile.endsWith(".tsx");
    const isJsOrTs = isTs || normFile.endsWith(".js") || normFile.endsWith(".jsx") || normFile.endsWith(".mjs") || normFile.endsWith(".cjs");
    const isMigration = normFile.includes("/Migrations/");

    if (isMigration) {
      if (normFile.endsWith("ModelSnapshot.cs")) {
        hasSnapshotFile = true;
      } else if (/\d{14}_[a-zA-Z0-9_]+\.cs$/.test(normFile)) {
        hasMigrationFile = true;
      }
      migrationFiles.push(normFile);
    }

    const approvedSecrets = isApprovedSecretPath(normFile);

    for (const { line } of addedLines) {
      // 1. Check for Moq usage in C# files
      if (isCs) {
        if (/\busing\s+Moq\b/.test(line) || /\bnew\s+Mock<[A-Za-z0-9_]+>/.test(line) || /\bMock\.Of<[A-Za-z0-9_]+>/.test(line)) {
          violations.push({
            rule: "no-moq",
            file: normFile,
            line: line.trim(),
            detail: "Use NSubstitute instead of Moq as specified in engineering standards.",
          });
        }
      }

      // 2. Check for unsupported TypeScript suppression directives in JS/TS files
      if (isJsOrTs) {
        if (TS_IGNORE_PATTERN.test(line)) {
          violations.push({
            rule: NO_TS_IGNORE_RULE,
            file: normFile,
            line: line.trim(),
            detail: `Use @ts-expect-error with documented rationale instead of ${TS_IGNORE_DIRECTIVE}.`,
          });
        }
      }

      // 3. Check for inline lint suppression directives
      if (isJsOrTs) {
        if (ESLINT_DISABLE_PATTERN.test(line)) {
          violations.push({
            rule: NO_ESLINT_DISABLE_RULE,
            file: normFile,
            line: line.trim(),
            detail: `${ESLINT_DISABLE_DIRECTIVE} directives are forbidden; fix the underlying lint violation.`,
          });
        }
      }

      // 4. Check for explicit unsafe 'any' in TypeScript
      if (isTs && !approvedSecrets) {
        // Match ': any' or 'as any' outside comments
        const cleanLine = line.replace(/\/\/.*$/, "").replace(/\/\*.*?\*\//g, "");
        if (/:\s*any\b/.test(cleanLine) || /\bas\s+any\b/.test(cleanLine)) {
          violations.push({
            rule: "no-explicit-any",
            file: normFile,
            line: line.trim(),
            detail: "Explicit 'any' is prohibited in production TypeScript code; use typed contracts or unknown.",
          });
        }
      }

      // 5. Check for high-confidence secrets outside test/fixture paths
      if (!approvedSecrets) {
        for (const pattern of HIGH_CONFIDENCE_SECRET_PATTERNS) {
          if (pattern.test(line)) {
            const lower = line.toLowerCase();
            const isPlaceholder = PLACEHOLDER_STRINGS.some((p) => lower.includes(p));
            if (!isPlaceholder) {
              violations.push({
                rule: "no-secrets",
                file: normFile,
                line: line.trim(),
                detail: "High-confidence credential or secret detected outside approved test fixture paths.",
              });
              break;
            }
          }
        }
      }
    }
  }

  // 6. EF Migration Guardrail
  if (migrationFiles.length > 0) {
    const isValidationApproved =
      env.MIGRATION_VALIDATED === "1" ||
      options.allowMigrations === true;

    // Flag suspicious mismatch: snapshot modified without migration file
    if (hasSnapshotFile && !hasMigrationFile) {
      violations.push({
        rule: "ef-migration-mismatch",
        file: migrationFiles.join(", "),
        line: "ModelSnapshot modified without accompanying migration file",
        detail: "EF Core snapshot changed without a new migration file. This may indicate an accidental manual snapshot edit.",
      });
    }

    if (!isValidationApproved) {
      violations.push({
        rule: "ef-migration-unverified",
        file: migrationFiles.join(", "),
        line: "Staged EF Core migration files",
        detail:
          "EF Core migrations require pre-commit verification per .agents/skills/add-migration/SKILL.md. Set MIGRATION_VALIDATED=1 once verified.",
      });
    }
  }

  return {
    ok: violations.length === 0,
    violations,
  };
}

export function checkDiff(diffText, options = {}) {
  const files = parseUnifiedDiff(diffText);
  return validateFiles(files, options);
}

export function main() {
  let diffText = "";
  try {
    diffText = execSync("git diff --cached -U0", { encoding: "utf8", maxBuffer: 10 * 1024 * 1024 });
  } catch (err) {
    console.error("pre-commit-guard: Failed to read staged diff:", err.message);
    process.exit(1);
  }

  if (!diffText.trim()) {
    process.exit(0);
  }

  const result = checkDiff(diffText);
  if (!result.ok) {
    console.error("\n❌ [pre-commit-guard] Blocked changes detected in staged diff:\n");
    for (const v of result.violations) {
      console.error(`  - [${v.rule}] ${v.file}`);
      console.error(`    Line: ${v.line}`);
      console.error(`    Why:  ${v.detail}\n`);
    }
    console.error("Please resolve the violations above before committing.\n");
    process.exit(1);
  }

  process.exit(0);
}

if (process.argv[1] && process.argv[1].replace(/\\/g, "/").endsWith("scripts/git/pre-commit-guard.mjs")) {
  main();
}
