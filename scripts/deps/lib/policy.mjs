import fs from "node:fs";
import { EXCEPTIONS_JSON, POLICY_JSON } from "./paths.mjs";

export function loadPolicy() {
  return JSON.parse(fs.readFileSync(POLICY_JSON, "utf8"));
}

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
const ECOSYSTEMS = new Set(["nuget", "npm"]);

export function validateExceptionEntry(entry, index) {
  const prefix = `dependency-exceptions.json exceptions[${index}]`;
  const errors = [];

  if (!entry || typeof entry !== "object") {
    return [`${prefix}: must be an object`];
  }

  if (!ECOSYSTEMS.has(entry.ecosystem)) {
    errors.push(`${prefix}: ecosystem must be nuget|npm`);
  }

  const hasName = typeof entry.name === "string" && entry.name.trim().length > 0;
  const hasPattern =
    typeof entry.namePattern === "string" && entry.namePattern.trim().length > 0;
  if (hasName === hasPattern) {
    errors.push(`${prefix}: provide exactly one of name or namePattern`);
  }

  if (!Array.isArray(entry.versions) || entry.versions.length === 0) {
    errors.push(`${prefix}: versions must be a non-empty array`);
  } else {
    const hasWildcard = entry.versions.includes("*");
    if (hasWildcard) {
      if (entry.allowVersionWildcard !== true) {
        errors.push(
          `${prefix}: versions may include '*' only when allowVersionWildcard is true`,
        );
      }
      if (typeof entry.wildcardReason !== "string" || entry.wildcardReason.trim().length < 20) {
        errors.push(`${prefix}: wildcardReason must explain why '*' is required`);
      }
    }
    for (const version of entry.versions) {
      if (typeof version !== "string" || !version.trim()) {
        errors.push(`${prefix}: versions entries must be non-empty strings`);
      }
    }
  }

  if (typeof entry.license !== "string" || !entry.license.trim()) {
    errors.push(`${prefix}: license is required`);
  }

  if (typeof entry.reason !== "string" || entry.reason.trim().length < 20) {
    errors.push(`${prefix}: reason must be a meaningful non-empty explanation`);
  }

  if (typeof entry.reviewedOn !== "string" || !ISO_DATE.test(entry.reviewedOn)) {
    errors.push(`${prefix}: reviewedOn must be an ISO date (YYYY-MM-DD)`);
  }

  if (
    entry.overrideDetectedLicense !== undefined &&
    typeof entry.overrideDetectedLicense !== "boolean"
  ) {
    errors.push(`${prefix}: overrideDetectedLicense must be a boolean when set`);
  }

  if (entry.overrideDetectedLicense === true) {
    if (typeof entry.license !== "string" || !entry.license.trim()) {
      errors.push(`${prefix}: overrideDetectedLicense requires license`);
    }
  }

  if (hasPattern) {
    try {
      // eslint-disable-next-line no-new
      new RegExp(entry.namePattern);
    } catch {
      errors.push(`${prefix}: namePattern is not a valid regular expression`);
    }
  }

  return errors;
}

export function loadExceptions() {
  const raw = JSON.parse(fs.readFileSync(EXCEPTIONS_JSON, "utf8"));
  if (!Array.isArray(raw.exceptions)) {
    throw new Error("dependency-exceptions.json must contain an exceptions array.");
  }

  const errors = raw.exceptions.flatMap((entry, index) => validateExceptionEntry(entry, index));
  if (errors.length > 0) {
    throw new Error(`Invalid dependency exceptions:\n - ${errors.join("\n - ")}`);
  }

  return raw.exceptions;
}

const ALIASES = new Map([
  ["mit", "MIT"],
  ["mit license", "MIT"],
  ["mit-0", "MIT-0"],
  ["apache-2.0", "Apache-2.0"],
  ["apache 2.0", "Apache-2.0"],
  ["apache license 2.0", "Apache-2.0"],
  ["apache license, version 2.0", "Apache-2.0"],
  ["the apache software license, version 2.0", "Apache-2.0"],
  ["bsd", "BSD-3-Clause"],
  ["bsd-2-clause", "BSD-2-Clause"],
  ["bsd-3-clause", "BSD-3-Clause"],
  ["bsd 2-clause", "BSD-2-Clause"],
  ["bsd 3-clause", "BSD-3-Clause"],
  ["bsd-3-clause-clear", "BSD-3-Clause-Clear"],
  ["new bsd", "BSD-3-Clause"],
  ["3-clause bsd license", "BSD-3-Clause"],
  ["2-clause bsd license", "BSD-2-Clause"],
  ["isc", "ISC"],
  ["isc license", "ISC"],
  ["0bsd", "0BSD"],
  ["bsd zero clause license", "0BSD"],
  ["postgresql", "PostgreSQL"],
  ["postgresql license", "PostgreSQL"],
  ["blueoak-1.0.0", "BlueOak-1.0.0"],
  ["unlicense", "Unlicense"],
  ["cc0-1.0", "CC0-1.0"],
  ["cc-by-4.0", "CC-BY-4.0"],
  ["mpl-2.0", "MPL-2.0"],
  ["mozilla public license 2.0", "MPL-2.0"],
  ["lgpl-2.1", "LGPL-2.1-only"],
  ["lgpl-2.1-only", "LGPL-2.1-only"],
  ["lgpl-2.1-or-later", "LGPL-2.1-or-later"],
  ["lgpl-3.0", "LGPL-3.0-only"],
  ["lgpl-3.0-only", "LGPL-3.0-only"],
  ["lgpl-3.0-or-later", "LGPL-3.0-or-later"],
  ["gpl-2.0", "GPL-2.0-only"],
  ["gpl-3.0", "GPL-3.0-only"],
  ["python-2.0", "Python-2.0"],
]);

const NUGET_LICENSE_URL = /^https?:\/\/licenses\.nuget\.org\/([^/?#]+)/i;

export function normalizeLicenseToken(raw) {
  if (!raw) {
    return null;
  }

  let value = String(raw).trim();
  if (!value || value === "UNLICENSED" || /^see license/i.test(value) || /^unknown$/i.test(value)) {
    return null;
  }

  const urlMatch = value.match(NUGET_LICENSE_URL);
  if (urlMatch) {
    value = decodeURIComponent(urlMatch[1]);
  } else if (/^https?:\/\//i.test(value)) {
    return null;
  }

  value = value.replace(/^license\s+/i, "").trim();
  const aliased = ALIASES.get(value.toLowerCase());
  return aliased ?? value;
}

function tokenizeExpression(expression) {
  const tokens = [];
  const source = expression.replace(/\//g, " OR ").replace(/\s+/g, " ").trim();
  const re = /\(|\)|OR|AND|[^\s()]+/gi;
  let match;
  while ((match = re.exec(source)) !== null) {
    tokens.push(match[0]);
  }
  return tokens;
}

function parseExpression(tokens) {
  let index = 0;

  function peek() {
    return tokens[index];
  }

  function consume(expected) {
    const token = tokens[index++];
    if (expected && (!token || token.toUpperCase() !== expected.toUpperCase())) {
      throw new Error(`Expected ${expected}`);
    }
    return token;
  }

  function parsePrimary() {
    const token = peek();
    if (!token) {
      throw new Error("Unexpected end of license expression");
    }
    if (token === "(") {
      consume("(");
      const node = parseOr();
      consume(")");
      return node;
    }
    consume();
    const normalized = normalizeLicenseToken(token);
    if (!normalized) {
      throw new Error(`Unknown license token: ${token}`);
    }
    return { type: "license", value: normalized };
  }

  function parseAnd() {
    let left = parsePrimary();
    while (peek() && peek().toUpperCase() === "AND") {
      consume("AND");
      left = { type: "and", left, right: parsePrimary() };
    }
    return left;
  }

  function parseOr() {
    let left = parseAnd();
    while (peek() && peek().toUpperCase() === "OR") {
      consume("OR");
      left = { type: "or", left, right: parseAnd() };
    }
    return left;
  }

  const ast = parseOr();
  if (index !== tokens.length) {
    throw new Error(`Unexpected token: ${tokens[index]}`);
  }
  return ast;
}

export function parseLicenseExpression(raw) {
  const normalizedWhole = normalizeLicenseToken(raw);
  if (!normalizedWhole) {
    return null;
  }

  if (!/[\s()/]|OR|AND/i.test(String(raw))) {
    return { type: "license", value: normalizedWhole };
  }

  try {
    return parseExpression(tokenizeExpression(String(raw)));
  } catch {
    return { type: "license", value: normalizedWhole };
  }
}

function evaluateAst(ast, isAllowedLeaf) {
  if (ast.type === "license") {
    return isAllowedLeaf(ast.value);
  }
  if (ast.type === "or") {
    return evaluateAst(ast.left, isAllowedLeaf) || evaluateAst(ast.right, isAllowedLeaf);
  }
  if (ast.type === "and") {
    return evaluateAst(ast.left, isAllowedLeaf) && evaluateAst(ast.right, isAllowedLeaf);
  }
  return false;
}

export function collectLicenseIds(ast, out = new Set()) {
  if (!ast) {
    return out;
  }
  if (ast.type === "license") {
    out.add(ast.value);
    return out;
  }
  collectLicenseIds(ast.left, out);
  collectLicenseIds(ast.right, out);
  return out;
}

export function isLicenseAllowed(rawLicense, allowedLicenses, exceptionLicenses = []) {
  const ast = parseLicenseExpression(rawLicense);
  if (!ast) {
    return false;
  }

  const allowed = new Set([...allowedLicenses, ...exceptionLicenses]);
  return evaluateAst(ast, (id) => allowed.has(id));
}

export function findException(exceptions, ecosystem, name, version) {
  return exceptions.find((entry) => {
    if (entry.ecosystem !== ecosystem) {
      return false;
    }

    const nameMatches = entry.namePattern
      ? new RegExp(entry.namePattern).test(name)
      : entry.name === name;
    if (!nameMatches) {
      return false;
    }

    const versions = entry.versions ?? [];
    return versions.includes("*") || versions.includes(version);
  });
}
