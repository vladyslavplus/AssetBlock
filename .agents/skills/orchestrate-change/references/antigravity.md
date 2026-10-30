# Optional Antigravity adapter

Read this only when the user explicitly selects the repository's Antigravity CLI adapter. The general orchestration skill has no provider or model default. This adapter remains available for compatibility; do not use it by default or change its scripts/schema as part of ordinary workflow guidance.

## Model and reasoning selection

The adapter currently defines DEFAULT_MODEL as gemini-3.7-flash-medium and uses it when --model and saved run state are absent. This is a runtime model fallback, not skill policy or a recommendation. Always pass --model with the exact identifier the user selected, on initial calls and continuations. Inspect identifiers supported by the installed CLI (for example, with agy models) before launch. If the selected model is unavailable, do not substitute another; report the blocker or ask the user to choose.

The adapter accepts --effort low, medium, or high. Pass it only when it matches the user's selected reasoning level. If the user leaves reasoning unspecified, preserve the current session configuration. The adapter run state records requested model and effort; it does not itself prove the provider's effective post-launch settings. Inspect provider/session evidence after launch when available. If effective settings cannot be confirmed, report that uncertainty rather than claiming the requested configuration ran.

A continuation reuses the saved conversation ID and model/effort state unless overridden. Inspect retained state and transcript before continuing. Do not mistake a completed prior report for a new job or retry a request without checking the conversation/queue state. No automatic model/provider switch is allowed after quota, failure, or availability errors.

## Adapter invocation and safety

Use the exact script and options supported by the installed version. Current adapter path is scripts/run-antigravity.mjs. It accepts a prompt file and, for non-dry runs, a permission manifest. The user-selected exact model must be explicit:

    node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agentflow/runs/<run-id>/executor-prompt.md --permission-manifest .agentflow/runs/<run-id>/permissions.json --model <selected-exact-model-id>

For continuation, reuse the authorized run directory and pass --model with the same user-selected identifier:

    node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agentflow/runs/<run-id>/fix-prompt.md --permission-manifest .agentflow/runs/<run-id>/fix-permissions.json --run-dir .agentflow/runs/<run-id> --model <selected-exact-model-id>

Invoke the dependency-free Node script directly, not through a package manager. For non-dry runs, provide the adapter-specific permission manifest: repository-relative read/write paths, exact allowed commands, any narrowly scoped command patterns, required verification, and path postconditions when acceptance depends on file creation/removal. The adapter checks this manifest against Antigravity settings before launch. Command patterns use fixed token arity; a pattern such as .* matches one token, not an arbitrary tail. Its permission manifest is adapter-specific; do not impose it on other transports. Keep allowed paths and commands limited to the current task. Do not use --dangerously-skip-permissions or edit global permissions as a workaround. The adapter uses Antigravity file-edit tools for repository source; do not authorize shell-writing fallbacks for that transport.

A --dry-run checks local setup and permission coverage without starting an agent turn. It does not verify implementation or model behavior. For each real attempt, retain and inspect current-run provider output/transcript, command evidence, changed-file ledger, and baseline/post-run Git evidence against the run and conversation identifiers. A completed earlier report is not evidence that a later request ran. Run-specific Git evidence and normalized execution reports are useful records, not proof of correctness.

## Adapter behavior boundaries

The adapter writes requested model/effort to its state and returns those options with the report. It records raw provider output and Git evidence, but its returned requested settings are not independent evidence of effective runtime settings. Check provider output/session metadata before claiming the selected configuration was active.

The adapter's final report schema and permission-manifest rules belong to this transport only. Read references/execution-report.schema.json when preparing its report. Its completion status is subject to the adapter's report checks and still does not prove behavior or independent review approval. Do not copy Antigravity-specific schema or permission requirements into generic workflow prompts or manifests.
