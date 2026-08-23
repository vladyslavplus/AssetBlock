import { defineConfig, devices } from '@playwright/test'

const port = Number(process.env.PLAYWRIGHT_PORT ?? 3100)
const baseURL = `http://127.0.0.1:${port}`

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    locale: 'en-US',
    viewport: { width: 1280, height: 720 },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'node e2e/stub-api.mjs',
      url: 'http://127.0.0.1:3999/health',
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: process.env.CI
        ? `pnpm exec next start --port ${port}`
        : `pnpm exec next dev --port ${port}`,
      url: baseURL,
      reuseExistingServer: false,
      timeout: 120_000,
      env: {
        PORT: String(port),
        NEXT_PUBLIC_API_BASE_URL: 'http://127.0.0.1:3999',
        ASSETBLOCK_API_BASE_URL: 'http://127.0.0.1:3999',
      },
    },
  ],
})
