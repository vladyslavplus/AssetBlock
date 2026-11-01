import { defineConfig, devices } from '@playwright/test'

const port = Number(process.env.PLAYWRIGHT_PORT ?? 3100)
// localhost (not 127.0.0.1): next dev normalizes request.url to localhost, so
// BFF same-origin checks only match when the browser origin is localhost too.
const baseURL = `http://localhost:${port}`

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
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
      command:
        'node --experimental-strip-types --experimental-loader ./e2e/schema-loader.mjs e2e/stub-api.mjs',
      url: 'http://127.0.0.1:3999/health',
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'node e2e/next-server.mjs',
      url: baseURL,
      reuseExistingServer: false,
      timeout: 180_000,
    },
  ],
})
