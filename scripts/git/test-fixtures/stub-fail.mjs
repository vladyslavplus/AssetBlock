#!/usr/bin/env node
import fs from "node:fs";

const logPath = process.env.LINT_STAGED_STUB_LOG;
if (logPath) {
  fs.appendFileSync(logPath, `${JSON.stringify({ tool: "fail", argv: process.argv.slice(2) })}\n`, "utf8");
}

process.exit(42);
