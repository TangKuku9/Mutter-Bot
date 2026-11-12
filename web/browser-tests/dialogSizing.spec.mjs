import { test, expect } from '@playwright/test'
import { defaultProactiveConfig } from '../src/proactiveParameters.js'

const group = { groupOpenId: 'synthetic-group', name: '回归测试群', addedAt: '2026-11-11T00:00:00Z', config: { isEnabled: true, aiProactiveEnabled: true } }
const fixtures = {
  '/api/status': { isConnected: false },
  '/api/groups': [group],
  '/api/groups/quotas': [],
  '/api/config/characters': [],
  '/api/proactive/config': { ...defaultProactiveConfig(), enabled: true },
  '/api/proactive/status': { enabled: true, participatingGroups: 1, todaySent: 0, todayDecisions: 0, pendingWindows: [], quotas: [] },
  '/api/proactive/logs': { items: [], totalCount: 0 },
  '/api/proactive/presets': [],
  '/api/proactive/groups/synthetic-group/config': { isEnabled: true, enabled: true, memoryAllowed: true, overrides: {} }
}

async function mockApi(page) {
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', route => {
    const pathname = new URL(route.request().url()).pathname
    return route.fulfill({ json: fixtures[pathname] ?? {} })
  })
}

async function navigateToGroups(page) {
  if (await page.locator('.panel-sidebar').isVisible()) {
    await page.locator('.panel-sidebar').getByRole('menuitem', { name: '群管理', exact: true }).click()
  } else {
    if (!await page.locator('.panel-mobile-drawer').isVisible()) await page.getByRole('button', { name: '打开导航', exact: true }).click()
    await page.locator('.panel-mobile-drawer').getByRole('menuitem', { name: '群管理', exact: true }).click()
  }
}

async function expectWidth(locator, cap, viewport) {
  await expect(locator).toBeVisible()
  await expect.poll(async () => {
    const bounds = await locator.boundingBox()
    return Math.abs(bounds.width - Math.min(cap, viewport - 32))
  }).toBeLessThanOrEqual(1)
  const bounds = await locator.boundingBox()
  expect(bounds.x).toBeGreaterThanOrEqual(15)
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(viewport - 15)
}

for (const darkMode of [false, true]) {
  for (const width of [1920, 1440, 390, 320]) {
    test(`弹窗保持独立宽度：${width}px · ${darkMode ? '深色' : '浅色'}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1080 })
      await page.addInitScript(darkMode => localStorage.setItem('mutter-panel-settings', JSON.stringify({ primaryColor: '#4f46e5', darkMode, compact: false })), darkMode)
      await mockApi(page)
      await page.goto('/#/proactive')
      const parameters = page.locator('.proactive-parameters')
      await parameters.getByRole('button', { name: '预设库', exact: true }).click()
      // Dialog keeps its declared 600px width; it must not inherit the MessageBox cap.
      const library = page.getByRole('dialog', { name: '参数预设', exact: true })
      await expectWidth(library.locator('.el-dialog'), 600, width)
      await library.locator('.el-dialog__headerbtn').click()
      await expect(library).toBeHidden()

      await parameters.locator('.scope-picker .el-select').click()
      await page.getByRole('option', { name: '回归测试群', exact: true }).click()
      const source = parameters.locator('.parameter-field').filter({ hasText: '发言倾向' }).locator('.source-select')
      await expect(source.getByRole('combobox')).toBeEnabled()
      await source.click()
      await page.getByRole('option', { name: '本群自定义', exact: true }).click()
      await expect(parameters.locator('.save-status')).toContainText('未保存')
      await navigateToGroups(page)
      const confirmation = page.locator('.el-message-box')
      await expectWidth(confirmation, 420, width)
      await confirmation.getByRole('button', { name: '继续编辑', exact: true }).click()
      await expect(confirmation).toBeHidden()
      await expect(page).toHaveURL(/#\/proactive$/)
      await expect(parameters.locator('.save-status')).toContainText('未保存')

      await navigateToGroups(page)
      await expectWidth(confirmation, 420, width)
      await confirmation.getByRole('button', { name: '放弃修改', exact: true }).click()
      await expect(page).toHaveURL(/#\/groups$/)
    })
  }
}
