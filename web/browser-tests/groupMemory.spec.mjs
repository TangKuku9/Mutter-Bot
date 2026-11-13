import { test, expect } from '@playwright/test'

async function setup(page, { failLoad = false } = {}) {
  const config = { enabled: false, countEnabled: true, count: 50, intervalEnabled: true, intervalMinutes: 120, scheduleEnabled: false, schedules: [{ frequency: 'daily', time: '22:00', weekday: 1 }], providerConnectionId: '', modelName: '' }
  const state = { config, revision: 0, writes: [], failLoad, items: [{ id: 1, content: '合成成员自述喜欢喝茶。', tags: ['饮品'], lastSeenAt: '2026-11-14T12:00:00Z', expiresAt: '2027-01-01T00:00:00Z' }] }
  const payload = () => ({ config: structuredClone(state.config), revision: state.revision, items: state.items, runs: [], status: { pending: 0, callsToday: 0, lastOutcome: state.revision ? 'baseline_set' : 'not_enabled', groupEnabled: true } })
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname, method = request.method()
    if (path === '/api/groups') return route.fulfill({ json: [{ groupOpenId: 'GROUP', name: '合成测试群' }] })
    if (path === '/api/config/ai/connections') return route.fulfill({ json: [
      { id: 'memory-provider', name: '独立总结连接', protocol: 'openai', models: [{ id: 'memory-small', name: 'memory-small' }], defaultModel: 'memory-small', hasApiKey: true },
      { id: 'other-provider', name: '另一总结连接', protocol: 'gemini', models: [{ id: 'memory-other', name: 'memory-other' }], defaultModel: 'memory-other', hasApiKey: true }
    ] })
    if (path === '/api/groups/GROUP/memory-auto') {
      if (method === 'GET') return state.failLoad ? route.fulfill({ status: 503, json: { error: '合成读取失败' } }) : route.fulfill({ json: payload() })
      state.writes.push({ path, method, body: request.postDataJSON() })
      if (method === 'PUT') { Object.assign(state.config, request.postDataJSON().config); state.revision++ }
      if (method === 'DELETE') { state.items = []; state.revision++ }
      return route.fulfill({ json: payload() })
    }
    if (path === '/api/groups/GROUP/memory-auto/1' && method === 'DELETE') {
      state.writes.push({ path, method }); state.items = []; return route.fulfill({ json: {} })
    }
    if (path === '/api/groups/GROUP/memory-auto/1/sources') return route.fulfill({ json: [{ id: 99, userName: '合成成员', plainText: '我喜欢喝茶。', createdAt: '2026-11-14T12:00:00Z' }] })
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${method} ${path}` } })
  })
  await page.goto('/#/group-memory?group=GROUP')
  if (!failLoad) await expect(page.getByRole('button', { name: '保存设置', exact: true })).toBeVisible()
  return state
}

test('旧环境打开页面不写配置，三种触发与独立模型一起保存', async ({ page }) => {
  const state = await setup(page)
  expect(state.writes).toEqual([])
  await expect(page.getByText('首次开启只学习开启后的新消息', { exact: false })).toBeVisible()
  await page.getByLabel('自动学习', { exact: true }).locator('..').click()
  await page.getByText('按定时任务', { exact: true }).click()
  await page.getByRole('button', { name: '添加时间', exact: true }).click()
  await expect(page.getByLabel('定时时刻 2', { exact: true })).toBeVisible()
  await page.getByTestId('memory-connection-select').click()
  await page.getByRole('option', { name: '独立总结连接', exact: false }).click()
  await page.getByLabel('手动填写记忆模型 ID', { exact: true }).fill('custom-memory-model')
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await expect(page.getByText('群记忆设置已保存', { exact: true })).toBeVisible()
  expect(state.writes).toHaveLength(1)
  expect(state.writes[0].body).toMatchObject({ revision: 0, config: { enabled: true, countEnabled: true, intervalEnabled: true, scheduleEnabled: true, providerConnectionId: 'memory-provider', modelName: 'custom-memory-model' } })
  expect(state.writes[0].body.config.schedules).toHaveLength(2)
  expect(state.writes.some(write => write.path === '/api/config/ai')).toBe(false)
})

test('记忆来源和逐条删除可用，窄屏不溢出', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = await setup(page)
  await page.getByRole('tab', { name: '记忆条目 1', exact: true }).click()
  await page.getByRole('button', { name: '查看来源', exact: true }).click()
  await expect(page.getByRole('dialog', { name: '记忆来源' })).toContainText('我喜欢喝茶。')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: '记忆来源' })).toBeHidden()
  await page.getByRole('button', { name: '删除记忆 1', exact: true }).click()
  await expect(page.getByText('还没有自动记忆', { exact: false })).toBeVisible()
  expect(state.writes).toHaveLength(1)
  expect(await page.locator('.memory-page').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
})

test('读取失败不会出现可保存的空配置', async ({ page }) => {
  const state = await setup(page, { failLoad: true })
  await expect(page.getByText('记忆配置读取失败', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '保存设置', exact: true })).toHaveCount(0)
  expect(state.writes).toEqual([])
})
