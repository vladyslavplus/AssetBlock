import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream, constants as fsConstants } from "node:fs";
import {
  access,
  copyFile,
  lstat,
  mkdir,
  readFile,
  readlink,
  rename,
  writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_PATH = fileURLToPath(import.meta.url);
const SCRIPT_DIR = path.dirname(SCRIPT_PATH);
const DEFAULT_SCHEMA = path.resolve(
  SCRIPT_DIR,
  "..",
  "references",
  "execution-report.schema.json",
);
export const DEFAULT_MODEL = "gemini-3.7-flash-medium";
const MAX_UNTRACKED_FILES = 10_000;
const MAX_UNTRACKED_BYTES = 512 * 1024 * 1024;

function pathPattern(value) {
  return value
    .split(/[\\/]+/u)
    .map((segment) => segment.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&"))
    .join("(?:\\\\+|/+)");
}

export function redactMachineLocalPaths(
  value,
  workspaceRoot = process.cwd(),
  userHome = os.homedir(),
) {
  let redacted = String(value);
  for (const [localPath, placeholder] of [
    [workspaceRoot, "workspace-root"],
    [userHome, "user-home"],
  ]) {
    if (!localPath) continue;
    redacted = redacted.replace(
      new RegExp(pathPattern(path.resolve(localPath)), "giu"),
      placeholder,
    );
  }
  return redacted;
}

export function buildExecutionPrompt(prompt, workspaceRoot = process.cwd()) {
  return [
    `Execution workspace root: ${path.resolve(workspaceRoot)}`,
    "Resolve every repository-relative path against that workspace root. Antigravity tool paths must target that workspace, not the CLI scratch directory. Do not run a shell command only to discover the working directory.",
    "",
    prompt,
  ].join(os.EOL);
}

export function parseArgs(argv) {
  const options = {
    timeout: "15m",
    schema: DEFAULT_SCHEMA,
    dryRun: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];

    if (argument === "--dry-run") {
      options.dryRun = true;
      continue;
    }

    const supported = new Map([
      ["--prompt-file", "promptFile"],
      ["--run-dir", "runDir"],
      ["--agy-bin", "agyBin"],
      ["--schema", "schema"],
      ["--timeout", "timeout"],
      ["--model", "model"],
      ["--effort", "effort"],
      ["--agent", "agent"],
      ["--permission-manifest", "permissionManifest"],
      ["--agy-settings", "agySettings"],
    ]);

    const key = supported.get(argument);
    if (!key) {
      throw new Error(`Unknown argument: ${argument}`);
    }

    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`Missing value for ${argument}`);
    }

    options[key] = value;
    index += 1;
  }

  if (!options.promptFile) {
    throw new Error("--prompt-file is required");
  }

  if (!options.dryRun && !options.permissionManifest) {
    throw new Error("--permission-manifest is required for non-dry runs");
  }

  if (options.effort && !["low", "medium", "high"].includes(options.effort)) {
    throw new Error("--effort must be low, medium, or high");
  }

  return options;
}

function normalizeCommand(value) {
  return value.trim().replace(/\s+/gu, " ");
}

function stripPathGlob(value) {
  return value.replace(/[\\/]\*\*$/u, "").replace(/[\\/]\*$/u, "");
}

function normalizePermissionPath(value) {
  const normalized = path.resolve(stripPathGlob(value));
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function permissionPathCovers(allowedPath, requiredPath) {
  const allowed = normalizePermissionPath(allowedPath);
  const required = normalizePermissionPath(requiredPath);
  return required === allowed || required.startsWith(`${allowed}${path.sep}`);
}

function parsePermissionRule(rule) {
  const match = /^(read_file|write_file|command)\(([\s\S]*)\)$/u.exec(rule);
  return match ? { kind: match[1], value: match[2] } : null;
}

function tokenizeCommand(value) {
  return normalizeCommand(value).match(/"[^"]*"|'[^']*'|\S+/gu) ?? [];
}

function commandRuleCovers(ruleValue, command) {
  const ruleTokens = tokenizeCommand(ruleValue);
  const commandTokens = tokenizeCommand(command);
  if (ruleTokens.length !== commandTokens.length) {
    return false;
  }
  return ruleTokens.every((pattern, index) => {
    if (pattern === commandTokens[index]) {
      return true;
    }
    try {
      return new RegExp(`^(?:${pattern})$`, "u").test(commandTokens[index]);
    } catch {
      return false;
    }
  });
}

export function inspectPermissionCoverage(settings, manifest) {
  const allow = settings?.permissions?.allow;
  if (!Array.isArray(allow)) {
    throw new Error("Antigravity settings do not contain permissions.allow");
  }

  const rules = allow.map(parsePermissionRule).filter(Boolean);
  const readRules = rules.filter((rule) => rule.kind === "read_file");
  const writeRules = rules.filter((rule) => rule.kind === "write_file");
  const commandRules = rules.filter((rule) => rule.kind === "command");
  const readPaths = manifest.read_paths ?? [];
  const writePaths = manifest.write_paths ?? [];
  const allowedCommands = manifest.allowed_commands ?? [];
  const allowedCommandPatterns = manifest.allowed_command_patterns ?? [];
  const requiredVerification = manifest.required_verification ?? [];
  const requiredPathsPresent = manifest.required_paths_present ?? [];
  const requiredPathsAbsent = manifest.required_paths_absent ?? [];

  for (const [name, value] of Object.entries({
    read_paths: readPaths,
    write_paths: writePaths,
    allowed_commands: allowedCommands,
    allowed_command_patterns: allowedCommandPatterns,
    required_verification: requiredVerification,
    required_paths_present: requiredPathsPresent,
    required_paths_absent: requiredPathsAbsent,
  })) {
    if (!Array.isArray(value) || value.some((entry) => typeof entry !== "string")) {
      throw new Error(`Permission manifest field ${name} must be an array of strings`);
    }
  }

  const allowedCommandSet = new Set(allowedCommands.map(normalizeCommand));
  const undeclaredVerification = requiredVerification.filter(
    (command) => !allowedCommandSet.has(normalizeCommand(command)),
  );
  if (undeclaredVerification.length > 0) {
    throw new Error(
      `required_verification contains command(s) absent from allowed_commands: ${undeclaredVerification.join("; ")}`,
    );
  }

  return {
    missing_read_paths: readPaths.filter(
      (required) =>
        !readRules.some((rule) => permissionPathCovers(rule.value, required)),
    ),
    missing_write_paths: writePaths.filter(
      (required) =>
        !writeRules.some((rule) => permissionPathCovers(rule.value, required)),
    ),
    missing_commands: allowedCommands.filter(
      (required) =>
        !commandRules.some((rule) => commandRuleCovers(rule.value, required)),
    ),
    missing_command_patterns: allowedCommandPatterns.filter(
      (required) =>
        !commandRules.some(
          (rule) => normalizeCommand(rule.value) === normalizeCommand(required),
        ),
    ),
  };
}

export async function inspectPathPostconditions(
  manifest,
  exists = pathExists,
) {
  const requiredPresent = manifest.required_paths_present ?? [];
  const requiredAbsent = manifest.required_paths_absent ?? [];
  const [presentResults, absentResults] = await Promise.all([
    Promise.all(requiredPresent.map(async (candidate) => [candidate, await exists(candidate)])),
    Promise.all(requiredAbsent.map(async (candidate) => [candidate, await exists(candidate)])),
  ]);
  return {
    missing_required_paths: presentResults
      .filter(([, pathIsPresent]) => !pathIsPresent)
      .map(([candidate]) => candidate),
    forbidden_paths_still_present: absentResults
      .filter(([, pathIsPresent]) => pathIsPresent)
      .map(([candidate]) => candidate),
  };
}

function formatPermissionGaps(coverage) {
  const lines = [];
  for (const [label, values] of [
    ["read paths", coverage.missing_read_paths],
    ["write paths", coverage.missing_write_paths],
    ["commands", coverage.missing_commands],
    ["command patterns", coverage.missing_command_patterns],
  ]) {
    if (values.length > 0) {
      lines.push(`${label}:`);
      lines.push(...values.map((value) => `- ${value}`));
    }
  }
  return lines.join(os.EOL);
}

export function resolveSessionOptions(options, state = {}) {
  return {
    model: options.model ?? state.model ?? DEFAULT_MODEL,
    effort: options.effort ?? state.effort ?? null,
    agent: options.agent ?? state.agent ?? null,
  };
}

async function isExecutableFile(candidate) {
  try {
    await access(candidate, fsConstants.X_OK);
    return true;
  } catch {
    return false;
  }
}

export async function resolveAgyBinary(options, environment = process.env) {
  const explicit = options.agyBin || environment.AGY_BIN;
  if (explicit) {
    const resolved = path.resolve(explicit);
    if (!(await isExecutableFile(resolved))) {
      throw new Error(`Antigravity executable is not accessible: ${resolved}`);
    }
    return resolved;
  }

  const command = process.platform === "win32" ? "agy.exe" : "agy";
  const searchPath = environment.Path ?? environment.PATH ?? "";
  for (const directory of searchPath.split(path.delimiter).filter(Boolean)) {
    const candidate = path.join(directory.replace(/^"|"$/gu, ""), command);
    if (await isExecutableFile(candidate)) {
      return candidate;
    }
  }

  if (process.platform === "win32" && environment.LOCALAPPDATA) {
    const standardWindowsPath = path.join(
      environment.LOCALAPPDATA,
      "agy",
      "bin",
      "agy.exe",
    );
    if (await isExecutableFile(standardWindowsPath)) {
      return standardWindowsPath;
    }
  }

  throw new Error(
    "Antigravity executable was not found. Restart Codex after installation or set AGY_BIN.",
  );
}

export function normalizeAgyResult(envelope) {
  const conversationId =
    envelope.conversation_id ??
    envelope.conversationId ??
    envelope.session_id ??
    envelope.sessionId ??
    null;

  let report =
    envelope.structured_output ??
    envelope.result ??
    envelope.response ??
    envelope.output ??
    envelope;
  if (typeof report === "string") {
    try {
      report = JSON.parse(report);
    } catch {
      // Preserve text so the caller can diagnose an unexpected CLI contract.
    }
  }

  return { conversationId, report };
}

function classifyRepositoryLane(value) {
  if (/\basblock-backend\b|\bdotnet\b/iu.test(value)) {
    return "backend";
  }
  if (
    /\basblock-frontend\b|\bpnpm\b|\bnpm\b|\byarn\b|\bprettier\b|\beslint\b/iu.test(
      value,
    )
  ) {
    return "frontend";
  }
  return "global";
}

function gitCapture(cwd, args) {
  const result = spawnSync("git", args, {
    cwd,
    encoding: "utf8",
    windowsHide: true,
    maxBuffer: 50 * 1024 * 1024,
  });

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(result.stderr.trim() || `git ${args.join(" ")} failed`);
  }

  return result.stdout;
}

function hashFile(filePath) {
  return new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    const stream = createReadStream(filePath);
    stream.on("error", reject);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("end", () => resolve(hash.digest("hex")));
  });
}

async function captureUntrackedFiles(runDir, label, cwd, preserveContents) {
  const output = gitCapture(cwd, [
    "ls-files",
    "--others",
    "--exclude-standard",
    "-z",
  ]);
  const relativePaths = output.split("\0").filter(Boolean);
  if (preserveContents && relativePaths.length > MAX_UNTRACKED_FILES) {
    throw new Error(
      `Baseline contains ${relativePaths.length} untracked files; limit is ${MAX_UNTRACKED_FILES}.`,
    );
  }
  const manifest = [];
  let totalBytes = 0;

  for (const relativePath of relativePaths) {
    const absolutePath = path.resolve(cwd, relativePath);
    if (absolutePath !== cwd && !absolutePath.startsWith(`${cwd}${path.sep}`)) {
      throw new Error(`Untracked path escapes repository: ${relativePath}`);
    }

    const metadata = await lstat(absolutePath);
    if (metadata.isSymbolicLink()) {
      manifest.push({
        path: relativePath,
        type: "symlink",
        target: await readlink(absolutePath),
      });
      continue;
    }
    if (!metadata.isFile()) {
      continue;
    }

    totalBytes += metadata.size;
    if (preserveContents && totalBytes > MAX_UNTRACKED_BYTES) {
      throw new Error(
        `Untracked baseline exceeds ${MAX_UNTRACKED_BYTES} bytes; clean or ignore large local artifacts before orchestration.`,
      );
    }

    const entry = {
      path: relativePath,
      type: "file",
      size: metadata.size,
      sha256: await hashFile(absolutePath),
    };
    if (preserveContents) {
      const backupPath = path.join(
        runDir,
        "baseline-untracked",
        "files",
        relativePath,
      );
      await mkdir(path.dirname(backupPath), { recursive: true });
      await copyFile(absolutePath, backupPath);
      entry.backup = path.relative(runDir, backupPath);
    }
    manifest.push(entry);
  }

  await writeFile(
    path.join(runDir, `${label}-untracked.json`),
    `${JSON.stringify(manifest, null, 2)}${os.EOL}`,
    "utf8",
  );
  return manifest;
}

export function compareUntrackedManifests(baseline, current) {
  const baselineByPath = new Map(baseline.map((entry) => [entry.path, entry]));
  const currentByPath = new Map(current.map((entry) => [entry.path, entry]));
  const changes = [];

  for (const [relativePath, before] of baselineByPath) {
    const after = currentByPath.get(relativePath);
    if (!after) {
      changes.push({ path: relativePath, status: "deleted", backup: before.backup });
      continue;
    }
    const changed =
      before.type !== after.type ||
      before.sha256 !== after.sha256 ||
      before.target !== after.target;
    if (changed) {
      changes.push({ path: relativePath, status: "modified", backup: before.backup });
    }
  }

  for (const relativePath of currentByPath.keys()) {
    if (!baselineByPath.has(relativePath)) {
      changes.push({ path: relativePath, status: "added" });
    }
  }

  return changes;
}

async function writeGitEvidence(runDir, label, cwd, preserveUntracked = false) {
  const [status, diff, stagedDiff] = [
    gitCapture(cwd, ["status", "--short", "--untracked-files=all"]),
    gitCapture(cwd, ["diff", "--binary", "--no-ext-diff"]),
    gitCapture(cwd, ["diff", "--binary", "--cached", "--no-ext-diff"]),
  ];

  const [, , , currentUntracked] = await Promise.all([
    writeFile(
      path.join(runDir, `${label}-status.txt`),
      redactMachineLocalPaths(status, cwd),
      "utf8",
    ),
    writeFile(
      path.join(runDir, `${label}-diff.patch`),
      redactMachineLocalPaths(diff, cwd),
      "utf8",
    ),
    writeFile(
      path.join(runDir, `${label}-staged-diff.patch`),
      redactMachineLocalPaths(stagedDiff, cwd),
      "utf8",
    ),
    captureUntrackedFiles(runDir, label, cwd, preserveUntracked),
  ]);

  if (!preserveUntracked) {
    const baselinePath = path.join(runDir, "baseline-untracked.json");
    if (await pathExists(baselinePath)) {
      const baseline = JSON.parse(await readFile(baselinePath, "utf8"));
      const changes = compareUntrackedManifests(baseline, currentUntracked);
      await writeFile(
        path.join(runDir, `${label}-untracked-changes.json`),
        `${JSON.stringify(changes, null, 2)}${os.EOL}`,
        "utf8",
      );
    }
  }
}

function createRunId() {
  return new Date().toISOString().replaceAll(":", "-").replaceAll(".", "-");
}

async function readState(runDir) {
  try {
    return JSON.parse(await readFile(path.join(runDir, "state.json"), "utf8"));
  } catch (error) {
    if (error.code === "ENOENT") {
      return { conversation_id: null, rounds: [] };
    }
    throw error;
  }
}

async function writeJsonAtomic(filePath, value) {
  const temporaryPath = `${filePath}.${process.pid}.tmp`;
  await writeFile(
    temporaryPath,
    `${redactMachineLocalPaths(JSON.stringify(value, null, 2))}${os.EOL}`,
    "utf8",
  );
  await rename(temporaryPath, filePath);
}

async function writeState(runDir, state) {
  await writeJsonAtomic(path.join(runDir, "state.json"), state);
}

async function pathExists(candidate) {
  try {
    await access(candidate, fsConstants.F_OK);
    return true;
  } catch {
    return false;
  }
}

export function inspectAgyStream(stdout) {
  const events = [];
  const invalidLines = [];
  for (const line of stdout.split(/\r?\n/u).filter(Boolean)) {
    try {
      events.push(JSON.parse(line));
    } catch {
      invalidLines.push(line);
    }
  }
  const terminal = events.findLast((event) => event.event === "result");
  const conversationId =
    terminal?.result?.conversation_id ??
    events.find((event) => event.conversation_id)?.conversation_id ??
    events.find((event) => event.init?.conversation_id)?.init?.conversation_id ??
    null;

  const unresolvedPermissionDenials = new Map();
  const commandRuns = new Map();
  const commandAttempts = new Map();
  const successfulMutationPaths = new Set();
  const lastMutationStepIndexByLane = {
    global: null,
    backend: null,
    frontend: null,
  };
  const mutatingTools = new Set([
    "multi_replace_file_content",
    "notebook_edit",
    "replace_file_content",
    "sed_file",
    "write_to_file",
  ]);
  for (const event of events) {
    const step = event.step_update;
    if (step?.step_type !== "tool") {
      continue;
    }

    const tool = step.tool_name ?? step.tool_info?.name ?? "unknown";
    const parameters = step.tool_info?.parameters ?? {};
    const key = `${tool}:${JSON.stringify(parameters)}`;
    if (tool === "run_command" && parameters.CommandLine) {
      const command = normalizeCommand(parameters.CommandLine);
      commandAttempts.set(command, { command, state: step.state });
    }
    if (step.state === "DONE") {
      unresolvedPermissionDenials.delete(key);
      if (mutatingTools.has(tool)) {
        const lane = classifyRepositoryLane(JSON.stringify(parameters));
        lastMutationStepIndexByLane[lane] =
          step.step_index ?? lastMutationStepIndexByLane[lane];
        for (const [parameterName, parameterValue] of Object.entries(
          parameters,
        )) {
          if (
            typeof parameterValue === "string" &&
            /^(?:AbsolutePath|FilePath|TargetFile)$/iu.test(parameterName)
          ) {
            successfulMutationPaths.add(parameterValue);
          }
        }
      }
      if (tool === "run_command" && parameters.CommandLine) {
        const command = parameters.CommandLine.trim().replace(/\s+/gu, " ");
        const output = step.tool_info?.output ?? "";
        const isVerification =
          /^(?:dotnet\s+(?:build|test)|pnpm\b.*\b(?:build|check|lint|test|typecheck)\b|npm\b.*\b(?:build|lint|test)\b|yarn\b.*\b(?:build|lint|test)\b)/iu.test(
            command,
          );
        const failed =
          isVerification &&
          /Build FAILED\.|Test Run Failed\.|Failed!.*Failed:\s*[1-9]|\berror (?:CS|TS|NU)\d+|ELIFECYCLE|ERR_PNPM|exit(?:ed)? (?:with )?(?:exit )?code [1-9]/isu.test(
            output,
          );
        commandRuns.set(command, {
          command,
          lane: classifyRepositoryLane(command),
          stepIndex: step.step_index ?? null,
          failed,
        });
        if (
          /^(?:Move-Item\b|git\s+mv\b|mv\b)|\b(?:prettier\b.*--write|eslint\b.*--fix|dotnet\s+format\b)/iu.test(
            command,
          )
        ) {
          const lane = classifyRepositoryLane(command);
          lastMutationStepIndexByLane[lane] =
            step.step_index ?? lastMutationStepIndexByLane[lane];
        }
      }
      continue;
    }

    const message = step.tool_info?.error?.message ?? "";
    if (
      step.state === "ERROR" &&
      /permission check failed|user denied permission/iu.test(message)
    ) {
      unresolvedPermissionDenials.set(key, { tool, parameters, message });
    }
  }

  return {
    terminal: terminal?.result ?? null,
    conversationId,
    invalidLines,
    unresolvedPermissionDenials: [...unresolvedPermissionDenials.values()],
    commandRuns: [...commandRuns.values()],
    commandAttempts: [...commandAttempts.values()],
    successfulMutationPaths: [...successfulMutationPaths],
    failedVerificationCommands: [...commandRuns.values()].filter(
      (entry) => entry.failed,
    ),
    lastMutationStepIndexByLane,
  };
}

export function validateExecutionReport(
  report,
  streamInspection,
  requiredVerification = [],
  allowedCommands = [],
  allowedCommandPatterns = [],
) {
  const errors = [];
  if (!report || typeof report !== "object") {
    return ["Structured execution report is missing"];
  }

  const allowedCommandSet = new Set(allowedCommands.map(normalizeCommand));
  const unauthorizedCommandAttempts = (
    streamInspection.commandAttempts ?? []
  ).filter(
    (entry) =>
      !allowedCommandSet.has(normalizeCommand(entry.command)) &&
      !allowedCommandPatterns.some((pattern) =>
        commandRuleCovers(pattern, entry.command),
      ),
  );
  if (unauthorizedCommandAttempts.length > 0) {
    errors.push(
      `Executor attempted ${unauthorizedCommandAttempts.length} command(s) absent from permission manifest: ${unauthorizedCommandAttempts.map((entry) => entry.command).join("; ")}`,
    );
  }

  if ((streamInspection.unresolvedPermissionDenials ?? []).length > 0) {
    errors.push(
      `Execution followed ${streamInspection.unresolvedPermissionDenials.length} unresolved permission denial(s)`,
    );
  }

  const successfulMutationPaths = streamInspection.successfulMutationPaths ?? [];
  if (successfulMutationPaths.length > 0 && (report.files_changed ?? []).length === 0) {
    errors.push(
      `Execution report claims no changed files after ${successfulMutationPaths.length} successful file mutation(s)`,
    );
  }

  if (report.status === "completed") {
    const incompleteVerification = (report.verification ?? []).filter(
      (entry) => entry.status !== "passed",
    );
    if (incompleteVerification.length > 0) {
      errors.push(
        `Completed report contains ${incompleteVerification.length} failed or not-run verification entries`,
      );
    }
    if (report.needs_human_reason) {
      errors.push("Completed report still declares a human-decision reason");
    }
    if ((streamInspection.failedVerificationCommands ?? []).length > 0) {
      errors.push(
        `Completed report followed ${streamInspection.failedVerificationCommands.length} unresolved failed verification command(s)`,
      );
    }
    const reportedVerification = new Map(
      (report.verification ?? []).map((entry) => [
        normalizeCommand(entry.command),
        entry,
      ]),
    );
    const executedCommands = new Map(
      (streamInspection.commandRuns ?? []).map((entry) => [
        normalizeCommand(entry.command),
        entry,
      ]),
    );
    const missingRequiredReports = requiredVerification.filter((command) => {
      const entry = reportedVerification.get(normalizeCommand(command));
      return !entry || entry.status !== "passed";
    });
    if (missingRequiredReports.length > 0) {
      errors.push(
        `Completed report omits ${missingRequiredReports.length} required passed verification command(s): ${missingRequiredReports.join("; ")}`,
      );
    }
    const missingRequiredExecutions = requiredVerification.filter((command) => {
      const entry = executedCommands.get(normalizeCommand(command));
      return !entry || entry.failed;
    });
    if (missingRequiredExecutions.length > 0) {
      errors.push(
        `Completed report lacks raw successful execution for ${missingRequiredExecutions.length} required verification command(s): ${missingRequiredExecutions.join("; ")}`,
      );
    }
    const mutationSteps = streamInspection.lastMutationStepIndexByLane ?? {
      global: streamInspection.lastMutationStepIndex ?? null,
      backend: null,
      frontend: null,
    };
    if (Object.values(mutationSteps).some((stepIndex) => stepIndex !== null)) {
      const commandRuns = new Map(
        (streamInspection.commandRuns ?? []).map((entry) => [entry.command, entry]),
      );
      const staleVerification = (report.verification ?? []).filter((entry) => {
        if (entry.status !== "passed") {
          return false;
        }
        const command = entry.command.trim().replace(/\s+/gu, " ");
        const run = commandRuns.get(command);
        if (!run || run.stepIndex === null) {
          return false;
        }
        const lane = run.lane ?? classifyRepositoryLane(command);
        const relevantMutationStep = Math.max(
          mutationSteps.global ?? -1,
          lane === "global" ? mutationSteps.backend ?? -1 : -1,
          lane === "global" ? mutationSteps.frontend ?? -1 : -1,
          lane === "backend" ? mutationSteps.backend ?? -1 : -1,
          lane === "frontend" ? mutationSteps.frontend ?? -1 : -1,
        );
        return run.stepIndex < relevantMutationStep;
      });
      if (staleVerification.length > 0) {
        errors.push(
          `Completed report contains ${staleVerification.length} verification command(s) run before the last file mutation`,
        );
      }
    }
  }

  if (
    report.status === "blocked" &&
    (typeof report.needs_human_reason !== "string" ||
      report.needs_human_reason.trim().length === 0)
  ) {
    errors.push("Blocked report does not explain why human input is required");
  }

  return errors;
}

function defaultAgySettingsPath(environment = process.env) {
  if (environment.AGY_SETTINGS) {
    return path.resolve(environment.AGY_SETTINGS);
  }
  return path.join(os.homedir(), ".gemini", "antigravity-cli", "settings.json");
}

export function parseAgyStream(stdout) {
  const inspected = inspectAgyStream(stdout);
  const terminal = inspected.terminal;
  if (!terminal) {
    const error = new Error(
      "Antigravity stream did not include a terminal result event",
    );
    error.conversationId = inspected.conversationId;
    throw error;
  }
  return terminal;
}

function runProcess(binary, args, cwd, prompt) {
  return new Promise((resolve, reject) => {
    const child = spawn(binary, args, {
      cwd,
      env: process.env,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });

    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
      process.stderr.write(redactMachineLocalPaths(chunk, cwd));
    });
    child.once("error", reject);
    child.once("close", (code, signal) => {
      resolve({ code, signal, stdout, stderr });
    });
    child.stdin.end(
      `${JSON.stringify({
        event: "user",
        message: { content: prompt },
      })}${os.EOL}`,
    );
  });
}

export async function main(argv = process.argv.slice(2)) {
  const options = parseArgs(argv);
  const cwd = process.cwd();
  const promptPath = path.resolve(options.promptFile);
  const schemaPath = path.resolve(options.schema);
  const prompt = (await readFile(promptPath, "utf8")).trim();
  if (!prompt) {
    throw new Error(`Prompt file is empty: ${promptPath}`);
  }
  await access(schemaPath, fsConstants.R_OK);

  let permissionManifest = null;
  let permissionManifestPath = null;
  let agySettingsPath = null;
  let permissionCoverage = null;
  if (options.permissionManifest) {
    permissionManifestPath = path.resolve(options.permissionManifest);
    permissionManifest = JSON.parse(
      await readFile(permissionManifestPath, "utf8"),
    );
    agySettingsPath = path.resolve(
      options.agySettings ?? defaultAgySettingsPath(),
    );
    const agySettings = JSON.parse(await readFile(agySettingsPath, "utf8"));
    permissionCoverage = inspectPermissionCoverage(
      agySettings,
      permissionManifest,
    );
    const permissionGapCount =
      permissionCoverage.missing_read_paths.length +
      permissionCoverage.missing_write_paths.length +
      permissionCoverage.missing_commands.length +
      permissionCoverage.missing_command_patterns.length;
    if (permissionGapCount > 0) {
      throw new Error(
        `Antigravity permission preflight found ${permissionGapCount} missing rule(s). No executor was started.${os.EOL}${formatPermissionGaps(permissionCoverage)}`,
      );
    }
  }

  const agyBinary = await resolveAgyBinary(options);
  const runDir = path.resolve(
    options.runDir ?? path.join(cwd, ".agentflow", "runs", createRunId()),
  );
  const state = await readState(runDir);
  const sessionOptions = resolveSessionOptions(options, state);
  const round = state.rounds.length + 1;
  const roundLabel = `round-${String(round).padStart(2, "0")}`;

  const args = [
    "--input-format",
    "stream-json",
    "--output-format",
    "stream-json",
    "--json-schema",
    schemaPath,
    "--print-timeout",
    options.timeout,
  ];

  if (state.conversation_id) {
    args.push("--conversation", state.conversation_id);
  }
  args.push("--model", sessionOptions.model);
  if (sessionOptions.effort) {
    args.push("--effort", sessionOptions.effort);
  }
  if (sessionOptions.agent) {
    args.push("--agent", sessionOptions.agent);
  }

  if (options.dryRun) {
    const result = {
      ok: true,
      dry_run: true,
      agy_bin: agyBinary,
      cwd,
      prompt_file: promptPath,
      schema: schemaPath,
      run_dir: runDir,
      resumes_conversation: Boolean(state.conversation_id),
      model: sessionOptions.model,
      effort: sessionOptions.effort,
      agent: sessionOptions.agent,
      permission_manifest: permissionManifestPath,
      agy_settings: agySettingsPath,
      permission_preflight: permissionCoverage,
    };
    process.stdout.write(
      `${redactMachineLocalPaths(JSON.stringify(result, null, 2), cwd)}${os.EOL}`,
    );
    return result;
  }

  await mkdir(runDir, { recursive: true });
  const baselineMarkerPath = path.join(runDir, "baseline-complete.json");
  if (!(await pathExists(baselineMarkerPath))) {
    await writeGitEvidence(runDir, "baseline", cwd, true);
    await writeJsonAtomic(baselineMarkerPath, {
      completed_at: new Date().toISOString(),
      untracked_file_limit: MAX_UNTRACKED_FILES,
      untracked_byte_limit: MAX_UNTRACKED_BYTES,
    });
  }
  await writeFile(
    path.join(runDir, `${roundLabel}-prompt.md`),
    `${redactMachineLocalPaths(prompt, cwd)}${os.EOL}`,
    "utf8",
  );
  await writeFile(
    path.join(runDir, `${roundLabel}-permission-manifest.json`),
    `${redactMachineLocalPaths(JSON.stringify(permissionManifest, null, 2), cwd)}${os.EOL}`,
    "utf8",
  );

  const runningRound = {
    round,
    started_at: new Date().toISOString(),
    prompt_file: path.relative(cwd, promptPath),
    permission_manifest: path.relative(cwd, permissionManifestPath),
    status: "running",
  };
  const runningState = {
    conversation_id: state.conversation_id,
    model: sessionOptions.model,
    effort: sessionOptions.effort,
    agent: sessionOptions.agent,
    rounds: [...state.rounds, runningRound],
  };
  await writeState(runDir, runningState);

  let processResult;
  try {
    processResult = await runProcess(
      agyBinary,
      args,
      cwd,
      buildExecutionPrompt(prompt, cwd),
    );
  } catch (error) {
    runningRound.status = "spawn_error";
    runningRound.completed_at = new Date().toISOString();
    runningRound.error = error.message;
    await writeState(runDir, runningState);
    try {
      await writeGitEvidence(runDir, `${roundLabel}-after`, cwd);
    } catch (evidenceError) {
      runningRound.evidence_error = evidenceError.message;
      await writeState(runDir, runningState);
    }
    throw error;
  }

  let envelope;
  let streamError;
  let streamInspection;
  try {
    streamInspection = inspectAgyStream(processResult.stdout);
    envelope = parseAgyStream(processResult.stdout);
  } catch (error) {
    streamError = error;
    if (error.conversationId) {
      runningState.conversation_id =
        runningState.conversation_id ?? error.conversationId;
    }
    runningRound.status = "invalid_output";
    runningRound.completed_at = new Date().toISOString();
    runningRound.exit_code = processResult.code;
    runningRound.error = error.message;
    await writeState(runDir, runningState);
  }

  if (envelope) {
    const envelopeConversationId =
      envelope.conversation_id ?? envelope.conversationId ?? null;
    runningState.conversation_id =
      envelopeConversationId ?? runningState.conversation_id;
    runningRound.completed_at = new Date().toISOString();
    runningRound.exit_code = processResult.code;
    runningRound.status = envelope.status ?? "unknown";
    if (envelope.error) {
      runningRound.error = envelope.error;
    }
    await writeState(runDir, runningState);
  }

  try {
    await Promise.all([
      writeFile(
        path.join(runDir, `${roundLabel}-stdout.json`),
        redactMachineLocalPaths(processResult.stdout, cwd),
        "utf8",
      ),
      writeFile(
        path.join(runDir, `${roundLabel}-stderr.log`),
        redactMachineLocalPaths(processResult.stderr, cwd),
        "utf8",
      ),
      writeGitEvidence(runDir, `${roundLabel}-after`, cwd),
    ]);
  } catch (evidenceError) {
    runningRound.evidence_error = evidenceError.message;
    await writeState(runDir, runningState);
    throw new Error(
      `Antigravity finished, but post-run evidence capture failed: ${evidenceError.message}`,
    );
  }

  if (streamError) {
    throw new Error(
      `Antigravity returned invalid stream output. See ${path.join(runDir, `${roundLabel}-stdout.json`)}`,
    );
  }

  if (processResult.code !== 0 || envelope.status !== "SUCCESS") {
    throw new Error(
      `Antigravity ended with status ${envelope.status ?? "unknown"} and exit code ${
        processResult.code
      }: ${envelope.error || "no error details"}`,
    );
  }

  const normalized = normalizeAgyResult(envelope);
  if (!normalized.conversationId && !state.conversation_id) {
    throw new Error("Antigravity response did not include a conversation ID");
  }
  if (!normalized.report || typeof normalized.report !== "object") {
    throw new Error("Antigravity response did not include a structured execution report");
  }

  runningRound.execution_report_status = normalized.report.status ?? "unknown";
  const reportValidationErrors = validateExecutionReport(
    normalized.report,
    streamInspection,
    permissionManifest.required_verification,
    permissionManifest.allowed_commands,
    permissionManifest.allowed_command_patterns,
  );
  if (normalized.report.status === "completed") {
    const postconditions = await inspectPathPostconditions(permissionManifest);
    runningRound.path_postconditions = postconditions;
    if (postconditions.missing_required_paths.length > 0) {
      reportValidationErrors.push(
        `Completed report is missing ${postconditions.missing_required_paths.length} required path(s): ${postconditions.missing_required_paths.join("; ")}`,
      );
    }
    if (postconditions.forbidden_paths_still_present.length > 0) {
      reportValidationErrors.push(
        `Completed report left ${postconditions.forbidden_paths_still_present.length} required-absent path(s): ${postconditions.forbidden_paths_still_present.join("; ")}`,
      );
    }
  }
  if (reportValidationErrors.length > 0) {
    runningRound.status = "invalid_report";
    runningRound.report_validation_errors = reportValidationErrors;
  }
  await writeState(runDir, runningState);

  if (reportValidationErrors.length > 0) {
    throw new Error(
      `Antigravity execution report failed validation: ${reportValidationErrors.join("; ")}. See ${path.join(runDir, `${roundLabel}-stdout.json`)}`,
    );
  }

  const result = {
    ok: true,
    run_dir: path.relative(cwd, runDir),
    conversation_id: runningState.conversation_id,
    model: sessionOptions.model,
    effort: sessionOptions.effort,
    agent: sessionOptions.agent,
    round,
    report: normalized.report,
  };
  process.stdout.write(
    `${redactMachineLocalPaths(JSON.stringify(result, null, 2), cwd)}${os.EOL}`,
  );
  return result;
}

if (path.resolve(process.argv[1] ?? "") === SCRIPT_PATH) {
  main().catch((error) => {
    process.stderr.write(
      `agentflow: ${redactMachineLocalPaths(error.message)}${os.EOL}`,
    );
    process.exitCode = 1;
  });
}
