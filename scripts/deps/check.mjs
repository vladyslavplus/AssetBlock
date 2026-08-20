import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { generateAll } from "./generate.mjs";
import { listNugetVulnerabilities } from "./lib/nuget.mjs";
import { listNpmVulnerabilities } from "./lib/npm.mjs";
import { filterSevereVulnerabilities } from "./lib/notices.mjs";
import { writeNoticesDiagnostics } from "./lib/diff.mjs";
import {
  GENERATED_NOTICES_DIAG,
  GOVERNANCE_DIAG_DIR,
  NOTICES_DIFF_DIAG,
  NOTICES_PATH,
} from "./lib/paths.mjs";

function fail(messages) {
  console.error(messages.join("\n"));
  process.exitCode = 1;
}

const isDirectRun = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (isDirectRun) {
  const existingNotices = fs.existsSync(NOTICES_PATH) ? fs.readFileSync(NOTICES_PATH, "utf8") : null;

  // Regenerate notices/SBOMs without writing notices yet so we can diff.
  // deps:check must never modify the committed THIRD-PARTY-NOTICES.md.
  const { notices, licenseErrors, policy } = await generateAll({
    writeNotices: false,
    writeSboms: true,
  });

  const errors = [];

  if (licenseErrors.length > 0) {
    errors.push("License policy violations:");
    for (const item of licenseErrors) {
      errors.push(` - ${item}`);
    }
  }

  if (existingNotices === null) {
    errors.push(`Missing ${NOTICES_PATH}. Run pnpm deps:generate and commit the result.`);
  } else if (existingNotices !== notices) {
    const { diff, generatedPath, diffPath } = writeNoticesDiagnostics({
      existingNotices,
      generatedNotices: notices,
      outputDir: GOVERNANCE_DIAG_DIR,
      generatedPath: GENERATED_NOTICES_DIAG,
      diffPath: NOTICES_DIFF_DIAG,
    });
    errors.push(
      "THIRD-PARTY-NOTICES.md is out of date. Run `pnpm deps:generate` and commit the updated file.",
    );
    errors.push(`Wrote diagnostic generated notices: ${generatedPath}`);
    errors.push(`Wrote diagnostic notices diff: ${diffPath}`);
    errors.push("Notices diff (bounded):");
    errors.push(diff.trimEnd());
  }

  const vulns = [
    ...listNugetVulnerabilities(),
    ...listNpmVulnerabilities(),
  ];
  const severe = filterSevereVulnerabilities(vulns, policy);
  if (severe.length > 0) {
    errors.push("High/Critical vulnerabilities detected:");
    for (const finding of severe) {
      errors.push(
        ` - [${finding.ecosystem}] ${finding.name}@${finding.version} (${finding.severity}) ${finding.advisoryUrl ?? ""}`.trim(),
      );
    }
  }

  if (errors.length > 0) {
    fail(errors);
  } else {
    console.log("Dependency governance check passed.");
    console.log(`Packages reviewed via notices regeneration (${notices.split("\n").length} notice lines).`);
    console.log(`Vulnerability findings below High/Critical: ${vulns.length - severe.length}`);
  }
}
