#!/usr/bin/env node
import fs from "node:fs";

const logPath = process.env.LINT_STAGED_STUB_LOG;
if (!logPath) {
  console.error("LINT_STAGED_STUB_LOG is required");
  process.exit(1);
}

const entry = { tool: "dotnet", argv: process.argv.slice(2) };
fs.appendFileSync(logPath, `${JSON.stringify(entry)}\n`, "utf8");
process.exit(0);
