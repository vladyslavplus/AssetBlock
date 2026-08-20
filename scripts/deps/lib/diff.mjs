import fs from "node:fs";

/**
 * Build a bounded unified-style line diff for diagnostics.
 * Does not modify inputs; returns a string suitable for console/artifact output.
 */
export function buildBoundedLineDiff(beforeText, afterText, {
  contextLines = 2,
  maxHunks = 20,
  maxLines = 200,
  beforeLabel = "committed",
  afterLabel = "generated",
} = {}) {
  const before = String(beforeText ?? "").split(/\r?\n/);
  const after = String(afterText ?? "").split(/\r?\n/);

  // Drop trailing empty line produced by split on final newline.
  if (before.length > 0 && before[before.length - 1] === "") {
    before.pop();
  }
  if (after.length > 0 && after[after.length - 1] === "") {
    after.pop();
  }

  const edits = myersDiff(before, after);
  const hunks = groupHunks(edits, contextLines);
  const lines = [`--- ${beforeLabel}`, `+++ ${afterLabel}`];
  let hunkCount = 0;
  let lineCount = lines.length;

  for (const hunk of hunks) {
    if (hunkCount >= maxHunks || lineCount >= maxLines) {
      lines.push(
        `@@ truncated: showing ${hunkCount} hunk(s), ${lineCount} lines (limit ${maxHunks}/${maxLines}) @@`,
      );
      break;
    }
    hunkCount += 1;
    lines.push(
      `@@ -${hunk.beforeStart},${hunk.beforeCount} +${hunk.afterStart},${hunk.afterCount} @@`,
    );
    for (const row of hunk.rows) {
      if (lineCount >= maxLines) {
        lines.push(
          `@@ truncated: showing ${hunkCount} hunk(s), ${lineCount} lines (limit ${maxHunks}/${maxLines}) @@`,
        );
        return `${lines.join("\n")}\n`;
      }
      lines.push(`${row.prefix}${row.text}`);
      lineCount += 1;
    }
  }

  if (hunks.length === 0) {
    lines.push("@@ no line differences @@");
  }

  return `${lines.join("\n")}\n`;
}

export function writeNoticesDiagnostics({
  existingNotices,
  generatedNotices,
  outputDir,
  generatedPath,
  diffPath,
}) {
  fs.mkdirSync(outputDir, { recursive: true });
  fs.writeFileSync(generatedPath, generatedNotices, "utf8");
  const diff = buildBoundedLineDiff(existingNotices ?? "", generatedNotices, {
    beforeLabel: "THIRD-PARTY-NOTICES.md (committed)",
    afterLabel: "THIRD-PARTY-NOTICES.md (generated)",
  });
  fs.writeFileSync(diffPath, diff, "utf8");
  return { diff, generatedPath, diffPath };
}

function myersDiff(a, b) {
  // LCS-based edit script via dynamic programming (acceptable for notices size).
  const n = a.length;
  const m = b.length;
  const dp = Array.from({ length: n + 1 }, () => new Array(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }

  const edits = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      edits.push({ type: "equal", beforeLine: i + 1, afterLine: j + 1, text: a[i] });
      i += 1;
      j += 1;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      edits.push({ type: "remove", beforeLine: i + 1, afterLine: j + 1, text: a[i] });
      i += 1;
    } else {
      edits.push({ type: "add", beforeLine: i + 1, afterLine: j + 1, text: b[j] });
      j += 1;
    }
  }
  while (i < n) {
    edits.push({ type: "remove", beforeLine: i + 1, afterLine: j + 1, text: a[i] });
    i += 1;
  }
  while (j < m) {
    edits.push({ type: "add", beforeLine: i + 1, afterLine: j + 1, text: b[j] });
    j += 1;
  }
  return edits;
}

function groupHunks(edits, contextLines) {
  const changeIndexes = [];
  for (let idx = 0; idx < edits.length; idx++) {
    if (edits[idx].type !== "equal") {
      changeIndexes.push(idx);
    }
  }
  if (changeIndexes.length === 0) {
    return [];
  }

  const ranges = [];
  let start = Math.max(0, changeIndexes[0] - contextLines);
  let end = Math.min(edits.length, changeIndexes[0] + contextLines + 1);
  for (let c = 1; c < changeIndexes.length; c++) {
    const nextStart = Math.max(0, changeIndexes[c] - contextLines);
    const nextEnd = Math.min(edits.length, changeIndexes[c] + contextLines + 1);
    if (nextStart <= end) {
      end = nextEnd;
    } else {
      ranges.push([start, end]);
      start = nextStart;
      end = nextEnd;
    }
  }
  ranges.push([start, end]);

  return ranges.map(([rangeStart, rangeEnd]) => {
    const slice = edits.slice(rangeStart, rangeEnd);
    const first = slice[0];
    let beforeCount = 0;
    let afterCount = 0;
    const rows = [];
    for (const edit of slice) {
      if (edit.type === "equal") {
        rows.push({ prefix: " ", text: edit.text });
        beforeCount += 1;
        afterCount += 1;
      } else if (edit.type === "remove") {
        rows.push({ prefix: "-", text: edit.text });
        beforeCount += 1;
      } else {
        rows.push({ prefix: "+", text: edit.text });
        afterCount += 1;
      }
    }
    return {
      beforeStart: first.beforeLine,
      afterStart: first.afterLine,
      beforeCount,
      afterCount,
      rows,
    };
  });
}
