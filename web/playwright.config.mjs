import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './browser-tests',
  fullyParallel: true,
  workers: 2,
  reporter: 'list',
  outputDir: '../artifacts/panel-browser-tests',
  use: {
    baseURL: 'http://127.0.0.1:4179',
    headless: true,
    browserName: 'chromium',
    channel: process.env.MUTTER_TEST_BROWSER_CHANNEL || undefined,
    trace: 'retain-on-failure'
  },
  webServer: {
    command: 'npm run preview -- --host 127.0.0.1 --port 4179 --strictPort',
    url: 'http://127.0.0.1:4179',
    reuseExistingServer: !process.env.CI
  }
})
