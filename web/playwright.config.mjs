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
    // 始终启动本次构建的预览服务：复用旧 4179 会让回归跑到过期的构建产物。
    reuseExistingServer: false
  }
})
