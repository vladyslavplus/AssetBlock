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
const MAX_UNTRACKED_FILES = 10_000;
const MAX_UNTRACKED_BYTES = 512 * 1024 * 1024;

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

  if (options.effort && !["low", "medium", "high"].includes(options.effort)) {
    throw new Error("--effort must be low, medium, or high");
  }

  return options;
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
    writeFile(path.join(runDir, `${label}-status.txt`), status, "utf8"),
    writeFile(path.join(runDir, `${label}-diff.patch`), diff, "utf8"),
    writeFile(path.join(runDir, `${label}-staged-diff.patch`), stagedDiff, "utf8"),
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
    `${JSON.stringify(value, null, 2)}${os.EOL}`,
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

  return { terminal: terminal?.result ?? null, conversationId, invalidLines };
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
      process.stderr.write(chunk);
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

  const agyBinary = await resolveAgyBinary(options);
  const runDir = path.resolve(
    options.runDir ?? path.join(cwd, ".agentflow", "runs", createRunId()),
  );
  const state = await readState(runDir);
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
  if (options.model) {
    args.push("--model", options.model);
  }
  if (options.effort) {
    args.push("--effort", options.effort);
  }
  if (options.agent) {
    args.push("--agent", options.agent);
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
    };
    process.stdout.write(`${JSON.stringify(result, null, 2)}${os.EOL}`);
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
    `${prompt}${os.EOL}`,
    "utf8",
  );

  const runningRound = {
    round,
    started_at: new Date().toISOString(),
    prompt_file: path.relative(cwd, promptPath),
    status: "running",
  };
  const runningState = {
    conversation_id: state.conversation_id,
    rounds: [...state.rounds, runningRound],
  };
  await writeState(runDir, runningState);

  let processResult;
  try {
    processResult = await runProcess(agyBinary, args, cwd, prompt);
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
  try {
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
        processResult.stdout,
        "utf8",
      ),
      writeFile(
        path.join(runDir, `${roundLabel}-stderr.log`),
        processResult.stderr,
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
  await writeState(runDir, runningState);

  const result = {
    ok: true,
    run_dir: runDir,
    conversation_id: runningState.conversation_id,
    round,
    report: normalized.report,
  };
  process.stdout.write(`${JSON.stringify(result, null, 2)}${os.EOL}`);
  return result;
}

if (path.resolve(process.argv[1] ?? "") === SCRIPT_PATH) {
  main().catch((error) => {
    process.stderr.write(`agentflow: ${error.message}${os.EOL}`);
    process.exitCode = 1;
  });
}
