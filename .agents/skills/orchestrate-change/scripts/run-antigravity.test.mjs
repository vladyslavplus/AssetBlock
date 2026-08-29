import assert from "node:assert/strict";
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  DEFAULT_MODEL,
  compareUntrackedManifests,
  inspectPermissionCoverage,
  inspectPathPostconditions,
  inspectAgyStream,
  normalizeAgyResult,
  parseAgyStream,
  parseArgs,
  redactMachineLocalPaths,
  resolveAgyBinary,
  resolveSessionOptions,
  validateExecutionReport,
} from "./run-antigravity.mjs";

test("reusable orchestration files contain no machine-local absolute paths", async () => {
  const skillRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const reusableFiles = [
    "README.md",
    "SKILL.md",
    "references/execution-report.schema.json",
    "scripts/run-antigravity.mjs",
    "scripts/run-antigravity.test.mjs",
  ];
  const machineLocalPath = /(?<![A-Za-z])[A-Za-z]:[\\/]|\/(?:Users|home)\/[^/\s"']+/u;

  for (const relativePath of reusableFiles) {
    const contents = await readFile(path.join(skillRoot, relativePath), "utf8");
    assert.doesNotMatch(contents, machineLocalPath, relativePath);
  }
});

test("redactMachineLocalPaths removes raw and JSON-escaped local locations", () => {
  const workspace = ["X:", "Users", "developer", "project"].join("\\");
  const home = ["X:", "Users", "developer"].join("\\");
  const input = JSON.stringify({ cwd: workspace, settings: `${home}\\.tool` });
  const redacted = redactMachineLocalPaths(input, workspace, home);

  assert.equal(redacted.includes("developer"), false);
  assert.equal(redacted.includes("X:"), false);
  assert.match(redacted, /workspace-root/u);
  assert.match(redacted, /user-home/u);
});

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
    "--permission-manifest",
    "permissions.json",
    "--dry-run",
  ]);

  assert.equal(options.promptFile, "prompt.md");
  assert.equal(options.runDir, ".agentflow/run-1");
  assert.equal(options.timeout, "20m");
  assert.equal(options.effort, "high");
  assert.equal(options.permissionManifest, "permissions.json");
  assert.equal(options.dryRun, true);
});

test("parseArgs requires a permission manifest for executor runs", () => {
  assert.throws(
    () => parseArgs(["--prompt-file", "prompt.md"]),
    /--permission-manifest is required/,
  );
});

test("parseArgs rejects missing prompt", () => {
  assert.throws(() => parseArgs([]), /--prompt-file is required/);
});

test("resolveSessionOptions defaults to Gemini 3.7 Flash Medium", () => {
  assert.deepEqual(resolveSessionOptions({}, {}), {
    model: DEFAULT_MODEL,
    effort: null,
    agent: null,
  });
});

test("resolveSessionOptions preserves a run model and accepts an explicit override", () => {
  assert.equal(
    resolveSessionOptions({}, { model: "gemini-3.7-flash-high" }).model,
    "gemini-3.7-flash-high",
  );
  assert.equal(
    resolveSessionOptions(
      { model: "claude-sonnet-4-6" },
      { model: "gemini-3.7-flash-high" },
    ).model,
    "claude-sonnet-4-6",
  );
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

test("inspectAgyStream reports an unresolved permission denial", () => {
  const inspected = inspectAgyStream(
    [
      JSON.stringify({
        event: "step_update",
        step_update: {
          state: "ERROR",
          step_type: "tool",
          tool_name: "run_command",
          tool_info: {
            parameters: { CommandLine: "pnpm run lint" },
            error: {
              message:
                'permission check failed for command "pnpm run lint": user denied permission',
            },
          },
        },
      }),
      JSON.stringify({
        event: "result",
        result: { status: "SUCCESS", structured_output: { status: "completed" } },
      }),
    ].join("\n"),
  );

  assert.equal(inspected.unresolvedPermissionDenials.length, 1);
  assert.equal(inspected.unresolvedPermissionDenials[0].tool, "run_command");
});

test("inspectAgyStream treats repository move commands as mutations", () => {
  const inspected = inspectAgyStream(
    JSON.stringify({
      event: "step_update",
      step_update: {
        step_index: 12,
        state: "DONE",
        step_type: "tool",
        tool_name: "run_command",
        tool_info: {
          parameters: {
            CommandLine:
              "Move-Item asblock-backend/old.cs asblock-backend/new.cs",
          },
          output: "",
        },
      },
    }),
  );

  assert.equal(inspected.lastMutationStepIndexByLane.backend, 12);
});

test("validateExecutionReport rejects stale completed reports", () => {
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [{ command: "pnpm run lint", status: "passed" }],
    },
    {
      unresolvedPermissionDenials: [{ tool: "run_command" }],
    },
  );

  assert.match(errors.join("\n"), /unresolved permission denial/);
});

test("validateExecutionReport rejects permission denial for blocked reports", () => {
  const errors = validateExecutionReport(
    {
      status: "blocked",
      needs_human_reason: "Need permission",
      files_changed: [],
      verification: [],
    },
    { unresolvedPermissionDenials: [{ tool: "run_command" }] },
  );

  assert.match(errors.join("\n"), /unresolved permission denial/);
});

test("validateExecutionReport rejects commands absent from manifest", () => {
  const errors = validateExecutionReport(
    {
      status: "blocked",
      needs_human_reason: "Command denied",
      files_changed: ["src/file.cs"],
      verification: [],
    },
    {
      unresolvedPermissionDenials: [],
      commandAttempts: [{ command: "dotnet format repo.sln", state: "ERROR" }],
      successfulMutationPaths: ["src/file.cs"],
    },
    [],
    ["dotnet test tests.csproj"],
  );

  assert.match(errors.join("\n"), /absent from permission manifest/);
});

test("validateExecutionReport accepts declared read-only command prefix", () => {
  const errors = validateExecutionReport(
    {
      status: "blocked",
      needs_human_reason: "Need input",
      files_changed: [],
      verification: [],
    },
    {
      unresolvedPermissionDenials: [],
      commandAttempts: [{ command: "git diff src/file.cs", state: "DONE" }],
    },
    [],
    [],
    ["git diff"],
  );

  assert.deepEqual(errors, []);
});

test("validateExecutionReport rejects empty file ledger after mutations", () => {
  const errors = validateExecutionReport(
    {
      status: "blocked",
      needs_human_reason: "Need input",
      files_changed: [],
      verification: [],
    },
    {
      unresolvedPermissionDenials: [],
      successfulMutationPaths: ["src/file.cs"],
    },
  );

  assert.match(errors.join("\n"), /claims no changed files/);
});

test("validateExecutionReport accepts a consistent completed report", () => {
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [{ command: "pnpm run lint", status: "passed" }],
    },
    { unresolvedPermissionDenials: [] },
  );

  assert.deepEqual(errors, []);
});

test("validateExecutionReport rejects omitted required verification", () => {
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [],
    },
    {
      unresolvedPermissionDenials: [],
      failedVerificationCommands: [],
      commandRuns: [],
    },
    ["dotnet test tests.csproj"],
  );

  assert.match(errors.join("\n"), /omits 1 required passed verification/);
  assert.match(errors.join("\n"), /lacks raw successful execution/);
});

test("validateExecutionReport accepts required verification with raw evidence", () => {
  const command = "dotnet test tests.csproj";
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [{ command, status: "passed" }],
    },
    {
      unresolvedPermissionDenials: [],
      failedVerificationCommands: [],
      commandRuns: [
        { command, lane: "backend", stepIndex: 10, failed: false },
      ],
      lastMutationStepIndexByLane: {
        global: null,
        backend: null,
        frontend: null,
      },
    },
    [command],
  );

  assert.deepEqual(errors, []);
});

test("inspectPermissionCoverage accepts ancestor file rules and exact commands", () => {
  const coverage = inspectPermissionCoverage(
    {
      permissions: {
        allow: [
          "read_file(workspace-root)",
          "write_file(workspace-root)",
          "command(dotnet test backend.csproj)",
        ],
      },
    },
    {
      read_paths: ["workspace-root/AGENTS.md"],
      write_paths: ["workspace-root/backend/**"],
      allowed_commands: ["dotnet test backend.csproj"],
      required_verification: ["dotnet test backend.csproj"],
    },
  );

  assert.deepEqual(coverage, {
    missing_read_paths: [],
    missing_write_paths: [],
    missing_commands: [],
    missing_command_prefixes: [],
    missing_command_patterns: [],
  });
});

test("inspectPermissionCoverage understands Antigravity command prefixes", () => {
  const coverage = inspectPermissionCoverage(
    { permissions: { allow: ["command(git diff)"] } },
    {
      read_paths: [],
      write_paths: [],
      allowed_commands: ["git diff -- src/file.cs"],
      allowed_command_prefixes: ["git diff"],
      required_verification: [],
    },
  );

  assert.deepEqual(coverage, {
    missing_read_paths: [],
    missing_write_paths: [],
    missing_commands: [],
    missing_command_prefixes: [],
    missing_command_patterns: [],
  });
});

test("inspectPermissionCoverage reports every exact command and path gap", () => {
  const coverage = inspectPermissionCoverage(
    {
      permissions: {
        allow: ["read_file(workspace-root)"],
      },
    },
    {
      read_paths: ["other-workspace/README.md"],
      write_paths: ["workspace-root/backend/**"],
      allowed_commands: ["dotnet test one.csproj", "git diff --check"],
      required_verification: ["dotnet test one.csproj"],
    },
  );

  assert.deepEqual(coverage, {
    missing_read_paths: ["other-workspace/README.md"],
    missing_write_paths: [
      "workspace-root/backend/**",
    ],
    missing_commands: ["dotnet test one.csproj", "git diff --check"],
    missing_command_prefixes: [],
    missing_command_patterns: [],
  });
});

test("inspectPathPostconditions reports missing and still-present paths", async () => {
  const existing = new Set(["present.cs", "old.cs"]);
  const result = await inspectPathPostconditions(
    {
      required_paths_present: ["present.cs", "missing.cs"],
      required_paths_absent: ["gone.cs", "old.cs"],
    },
    async (candidate) => existing.has(candidate),
  );

  assert.deepEqual(result, {
    missing_required_paths: ["missing.cs"],
    forbidden_paths_still_present: ["old.cs"],
  });
});

test("validateExecutionReport accepts declared scoped command pattern", () => {
  const command =
    "Move-Item asblock-backend\\old.cs asblock-backend\\new.cs";
  const errors = validateExecutionReport(
    {
      status: "blocked",
      needs_human_reason: "Need input",
      files_changed: [],
      verification: [],
    },
    {
      unresolvedPermissionDenials: [],
      commandAttempts: [{ command, state: "DONE" }],
    },
    [],
    [],
    [],
    ["Move-Item asblock-backend.* asblock-backend.*"],
  );

  assert.deepEqual(errors, []);
});

test("validateExecutionReport rejects a hidden failed verification command", () => {
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [{ command: "dotnet test tests.csproj", status: "passed" }],
    },
    {
      unresolvedPermissionDenials: [],
      failedVerificationCommands: [{ command: "dotnet test tests.csproj" }],
      commandRuns: [],
      lastMutationStepIndex: null,
    },
  );

  assert.match(errors.join("\n"), /failed verification command/);
});

test("validateExecutionReport rejects verification made stale by a later edit", () => {
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [{ command: "pnpm run lint", status: "passed" }],
    },
    {
      unresolvedPermissionDenials: [],
      failedVerificationCommands: [],
      commandRuns: [
        { command: "pnpm run lint", stepIndex: 10, failed: false },
      ],
      lastMutationStepIndex: 11,
    },
  );

  assert.match(errors.join("\n"), /before the last file mutation/);
});

test("validateExecutionReport keeps backend verification valid after a frontend-only edit", () => {
  const errors = validateExecutionReport(
    {
      status: "completed",
      needs_human_reason: null,
      verification: [
        { command: "dotnet test backend.csproj", status: "passed" },
      ],
    },
    {
      unresolvedPermissionDenials: [],
      failedVerificationCommands: [],
      commandRuns: [
        {
          command: "dotnet test backend.csproj",
          lane: "backend",
          stepIndex: 10,
          failed: false,
        },
      ],
      lastMutationStepIndexByLane: {
        global: null,
        backend: null,
        frontend: 11,
      },
    },
  );

  assert.deepEqual(errors, []);
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
