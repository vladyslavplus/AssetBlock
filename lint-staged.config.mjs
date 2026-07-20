import path from 'node:path'

const FRONTEND_DIR = 'asblock-frontend'

/** Paths relative to asblock-frontend/ so local eslint/prettier configs apply. */
function toFrontendPaths(files) {
  const prefix = `${FRONTEND_DIR}${path.sep}`
  const prefixPosix = `${FRONTEND_DIR}/`
  return files.map((file) => {
    const normalized = path.normalize(file)
    if (normalized.startsWith(prefix) || normalized.startsWith(prefixPosix)) {
      return normalized.slice(FRONTEND_DIR.length + 1).replace(/\\/g, '/')
    }
    const absFrontend = path.resolve(FRONTEND_DIR)
    if (path.resolve(normalized).startsWith(absFrontend + path.sep)) {
      return path.relative(FRONTEND_DIR, normalized).replace(/\\/g, '/')
    }
    return normalized.replace(/\\/g, '/')
  })
}

function quote(files) {
  return files.map((f) => `"${f.replace(/"/g, '\\"')}"`).join(' ')
}

/** @type {import('lint-staged').Configuration} */
export default {
  [`${FRONTEND_DIR}/**/*.{js,jsx,mjs,cjs,ts,tsx}`]: (files) => {
    const relative = toFrontendPaths(files)
    if (relative.length === 0) return []
    const list = quote(relative)
    return [
      `pnpm --dir ${FRONTEND_DIR} exec eslint --fix -- ${list}`,
      `pnpm --dir ${FRONTEND_DIR} exec prettier --write -- ${list}`,
    ]
  },
  [`${FRONTEND_DIR}/**/*.{json,css,md,yml,yaml}`]: (files) => {
    const relative = toFrontendPaths(files)
    if (relative.length === 0) return []
    return [`pnpm --dir ${FRONTEND_DIR} exec prettier --write -- ${quote(relative)}`]
  },
}
