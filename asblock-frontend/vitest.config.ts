import path from 'node:path'
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

const rootDir = path.dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: [
      { find: '@', replacement: rootDir },
      { find: 'server-only', replacement: path.join(rootDir, 'test/shims/server-only.ts') },
    ],
  },
  test: {
    globals: false,
    setupFiles: ['./test/setup.ts'],
    exclude: ['node_modules', '.next', 'e2e/**', 'playwright-report/**', 'test-results/**'],
    restoreMocks: true,
    clearMocks: true,
    mockReset: true,
    env: {
      NEXT_PUBLIC_API_BASE_URL: 'http://api.test',
      ASSETBLOCK_API_BASE_URL: 'http://api.test',
    },
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json-summary', 'html'],
      reportsDirectory: './coverage',
      include: [
        'lib/server/auth-cookies.ts',
        'lib/server/fetch-backend.ts',
        'lib/server/refresh-session.ts',
        'lib/server/bff-http.ts',
        'lib/http/api-client.ts',
        'lib/http/bff-json.ts',
        'lib/http/api-errors.ts',
        'lib/http/is-abort-error.ts',
        'lib/query/query-refresh.ts',
        'lib/query/clear-user-scoped-queries.ts',
      ],
      thresholds: {
        lines: 70,
        functions: 70,
        statements: 70,
        branches: 55,
      },
    },
    projects: [
      {
        extends: true,
        test: {
          name: 'node',
          environment: 'node',
          include: ['**/*.test.ts'],
        },
      },
      {
        extends: true,
        test: {
          name: 'jsdom',
          environment: 'jsdom',
          include: ['**/*.test.tsx'],
        },
      },
    ],
  },
})
