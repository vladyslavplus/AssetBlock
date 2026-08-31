import path from "node:path";

const FRONTEND_DIR = "asblock-frontend";
const BACKEND_DIR = "asblock-backend";

/** Paths relative to asblock-frontend/ so local eslint/prettier configs apply. */
function toFrontendPaths(files) {
  const prefix = `${FRONTEND_DIR}${path.sep}`;
  const prefixPosix = `${FRONTEND_DIR}/`;
  return files.map((file) => {
    const normalized = path.normalize(file);
    if (normalized.startsWith(prefix) || normalized.startsWith(prefixPosix)) {
      return normalized.slice(FRONTEND_DIR.length + 1).replace(/\\/g, "/");
    }
    const absFrontend = path.resolve(FRONTEND_DIR);
    if (path.resolve(normalized).startsWith(absFrontend + path.sep)) {
      return path.relative(FRONTEND_DIR, normalized).replace(/\\/g, "/");
    }
    return normalized.replace(/\\/g, "/");
  });
}

/** Repository-root-relative POSIX paths under asblock-backend/, excluding generated migrations. */
function toBackendPaths(files) {
  const rootDir = process.cwd();
  const result = [];
  const seen = new Set();

  for (const file of files) {
    const normalized = path.normalize(file);
    const absPath = path.isAbsolute(normalized)
      ? normalized
      : path.resolve(rootDir, normalized);
    const relToRoot = path.relative(rootDir, absPath).replace(/\\/g, "/");

    if (!relToRoot.startsWith(`${BACKEND_DIR}/`)) {
      continue;
    }

    if (relToRoot.includes("/Migrations/")) {
      continue;
    }

    if (!seen.has(relToRoot)) {
      seen.add(relToRoot);
      result.push(relToRoot);
    }
  }

  return result;
}

function quote(files) {
  return files.map((f) => `"${f.replace(/"/g, '\\"')}"`).join(" ");
}

/** @type {import('lint-staged').Configuration} */
export default {
  [`${FRONTEND_DIR}/**/*.{js,jsx,mjs,cjs,ts,tsx}`]: (files) => {
    const relative = toFrontendPaths(files);
    if (relative.length === 0) return [];
    const list = quote(relative);
    return [
      `pnpm --dir ${FRONTEND_DIR} exec eslint --fix -- ${list}`,
      `pnpm --dir ${FRONTEND_DIR} exec prettier --write -- ${list}`,
    ];
  },
  [`${FRONTEND_DIR}/**/*.{json,css,md,yml,yaml}`]: (files) => {
    const relative = toFrontendPaths(files);
    if (relative.length === 0) return [];
    return [
      `pnpm --dir ${FRONTEND_DIR} exec prettier --write -- ${quote(relative)}`,
    ];
  },
  [`${BACKEND_DIR}/**/*.cs`]: (files) => {
    const relative = toBackendPaths(files);
    if (relative.length === 0) return [];
    return [
      `dotnet format ${BACKEND_DIR}/asblock-backend.slnx --no-restore --include ${quote(relative)} --verbosity minimal`,
    ];
  },
};
