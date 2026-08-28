import assert from "node:assert/strict";
import { copyFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  compareUntrackedManifests,
  inspectAgyStream,
  normalizeAgyResult,
  parseAgyStream,
  parseArgs,
  resolveAgyBinary,
} from "./run-antigravity.mjs";

test("parseArgs accepts supported orchestration options", () => {
  const options = parseArgs([
    "--prompt-file",
    "prompt.md",
    "--run-dir",
    ".agentflow/run-1",
    "--timeout",
    "20m",
    "--effort",
    "high",
    "--dry-run",
  ]);

  assert.equal(options.promptFile, "prompt.md");
  assert.equal(options.runDir, ".agentflow/run-1");
  assert.equal(options.timeout, "20m");
  assert.equal(options.effort, "high");
  assert.equal(options.dryRun, true);
});

test("parseArgs rejects missing prompt", () => {
  assert.throws(() => parseArgs([]), /--prompt-file is required/);
});

test("normalizeAgyResult extracts a structured string result", () => {
  const normalized = normalizeAgyResult({
    conversation_id: "conversation-1",
    result: JSON.stringify({ status: "completed", summary: "Done" }),
  });

  assert.equal(normalized.conversationId, "conversation-1");
  assert.deepEqual(normalized.report, {
    status: "completed",
    summary: "Done",
  });
});

test("normalizeAgyResult prefers parsed structured output", () => {
  const normalized = normalizeAgyResult({
    conversation_id: "conversation-2",
    response: "{\"status\":\"blocked\"}",
    structured_output: { status: "completed", summary: "Done" },
  });

  assert.deepEqual(normalized.report, {
    status: "completed",
    summary: "Done",
  });
});

test("parseAgyStream returns the terminal result", () => {
  const result = parseAgyStream(
    [
      JSON.stringify({ event: "init", conversation_id: "conversation-3" }),
      JSON.stringify({
        event: "result",
        result: {
          conversation_id: "conversation-3",
          status: "SUCCESS",
          structured_output: { status: "completed" },
        },
      }),
    ].join("\n"),
  );

  assert.equal(result.conversation_id, "conversation-3");
  assert.equal(result.status, "SUCCESS");
});

test("inspectAgyStream preserves init conversation ID from a truncated stream", () => {
  const inspected = inspectAgyStream(
    `${JSON.stringify({ event: "init", conversation_id: "conversation-4" })}\n{`,
  );

  assert.equal(inspected.conversationId, "conversation-4");
  assert.equal(inspected.terminal, null);
  assert.deepEqual(inspected.invalidLines, ["{"]);
});

test("compareUntrackedManifests detects recoverable overwrite and deletion", () => {
  const changes = compareUntrackedManifests(
    [
      { path: "draft.txt", type: "file", sha256: "before", backup: "backup/draft.txt" },
      { path: "remove.txt", type: "file", sha256: "same", backup: "backup/remove.txt" },
    ],
    [
      { path: "draft.txt", type: "file", sha256: "after" },
      { path: "new.txt", type: "file", sha256: "new" },
    ],
  );

  assert.deepEqual(changes, [
    {
      path: "draft.txt",
      status: "modified",
      backup: "backup/draft.txt",
    },
    {
      path: "remove.txt",
      status: "deleted",
      backup: "backup/remove.txt",
    },
    { path: "new.txt", status: "added" },
  ]);
});

test("resolveAgyBinary honors an explicit inaccessible path", async () => {
  await assert.rejects(
    resolveAgyBinary({ agyBin: "missing-antigravity-binary" }, {}),
    /Antigravity executable is not accessible/,
  );
});

test("resolveAgyBinary rejects a missing PATH and fallback installation", async () => {
  await assert.rejects(
    resolveAgyBinary(
      {},
      {
        PATH: "",
        LOCALAPPDATA: "missing-local-app-data",
      },
    ),
    /Antigravity executable was not found/,
  );
});

test(
  "resolveAgyBinary prefers PATH over the standard Windows fallback",
  { skip: process.platform !== "win32" },
  async () => {
    const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), "agy-resolver-"));
    try {
      const pathDirectory = path.join(temporaryRoot, "path-bin");
      const localAppData = path.join(temporaryRoot, "local-app-data");
      const pathBinary = path.join(pathDirectory, "agy.exe");
      const fallbackBinary = path.join(localAppData, "agy", "bin", "agy.exe");
      await Promise.all([
        mkdir(pathDirectory, { recursive: true }),
        mkdir(path.dirname(fallbackBinary), { recursive: true }),
      ]);
      await Promise.all([
        copyFile(process.execPath, pathBinary),
        writeFile(fallbackBinary, "fallback"),
      ]);

      const resolved = await resolveAgyBinary(
        {},
        {
          PATH: pathDirectory,
          Path: pathDirectory,
          PATHEXT: ".EXE",
          LOCALAPPDATA: localAppData,
          SystemRoot: process.env.SystemRoot,
        },
      );

      assert.equal(resolved.toLowerCase(), pathBinary.toLowerCase());
    } finally {
      await rm(temporaryRoot, { recursive: true, force: true });
    }
  },
);
