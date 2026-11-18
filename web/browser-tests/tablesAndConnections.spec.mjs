import { test, expect } from '@playwright/test'
import { normalizeAiSettings } from '../src/aiSettings.js'

// 定向回归：连接管理器的模型分页 / 模板切换 / 请求头行状态，
// 以及群列表、会话表、AI 统计在窄屏下的列可见性。每个测试组独立 fixture，避免一份巨型配置。

// —— 连接管理器（AI 设置页的「管理服务商」弹窗）——
const modelList = (prefix, count) => Array.from({ length: count }, (_, i) => {
  const id = `${prefix}-${String(i).padStart(2, '0')}`
  return { id, name: id, isManual: true }
})

async function openConnectionManager(page, { create = false, discoverModels = modelList('extra', 50) } = {}) {
  const state = {
    connections: [{
      id: 'primary', name: '主账号', protocol: 'openai', apiBaseUrl: 'https://model.example.test/v1',
      apiKey: '********', hasApiKey: true, defaultModel: 'batch-00', timeoutSeconds: 60,
      headers: { 'X-Saved': 'saved-value' }, models: modelList('batch', 105),
      updatedAt: '2026-11-13T00:00:00.000Z', revision: 1
    }],
    writes: [], discoverModels
  }
  const config = normalizeAiSettings({
    providerConnectionId: 'primary', connections: state.connections,
    apiBaseUrl: 'https://model.example.test/v1', apiKey: 'sk-****', modelName: 'batch-00',
    availableProviders: [
      { id: 'deepseek', name: 'DeepSeek', category: 'china', protocol: 'openai', apiBaseUrl: 'https://api.deepseek.com/v1', defaultModel: 'deepseek-flash', models: ['deepseek-flash', 'deepseek-v4-pro'] },
      { id: 'custom', name: '自定义服务商', category: 'custom', models: [] }
    ],
    availableCharacters: [],
    externalSearch: { baseUrl: '', apiKey: '' }
  })
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname, method = req.method()
    const body = () => req.postDataJSON()
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    if (path === '/api/config/network') return route.fulfill({ json: { allowSystemProxy: false } })
    if (path === '/api/config/characters') return route.fulfill({ json: [] })
    if (path === '/api/config/ai/presets') return route.fulfill({ json: [] })
    if (path === '/api/config/ai') return route.fulfill({ json: config })
    if (path === '/api/config/ai/connections' && method === 'GET') return route.fulfill({ json: state.connections })
    if (path === '/api/config/ai/connections/discover') return route.fulfill({ json: state.discoverModels })
    if (path === '/api/config/ai/connections/test') return route.fulfill({ json: { message: '模型调用成功', model: body().defaultModel, elapsedMs: 12 } })
    if (path === '/api/config/ai/connections' && method === 'POST') {
      state.writes.push({ path, method, body: body() })
      const saved = { ...body(), id: `connection-${state.connections.length}`, updatedAt: '2026-11-13T00:00:01.000Z', hasApiKey: !!body().apiKey, apiKey: body().apiKey ? '********' : '' }
      state.connections.push(saved)
      return route.fulfill({ json: saved })
    }
    if (path.startsWith('/api/config/ai/connections/') && method === 'PUT') {
      state.writes.push({ path, method, body: body() })
      const id = path.split('/').at(-1)
      const saved = { ...body(), id, updatedAt: '2026-11-13T00:00:02.000Z', hasApiKey: !!body().apiKey, apiKey: body().apiKey ? '********' : '' }
      const index = state.connections.findIndex(item => item.id === id)
      if (index >= 0) state.connections[index] = saved
      return route.fulfill({ json: saved })
    }
    return route.fulfill({ status: 404, json: { error: `测试：未处理的 API 请求 ${method} ${path}` } })
  })
  await page.goto('/#/ai-settings')
  await expect(page.getByTestId('model-select')).toContainText('batch-00')
  await page.getByRole('button', { name: create ? '添加服务商' : '管理服务商', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
  await expect(dialog).toBeVisible()
  return { state, dialog }
}

test('获取超过一页的模型后新增可见：翻到新增页、手动模型可见、搜索回第一页、删行不弹回', async ({ page }) => {
  test.slow() // 每页 100 行输入框渲染较慢
  const { state, dialog } = await openConnectionManager(page)
  const pager = dialog.locator('.connection-model-pager')
  await expect(pager.locator('li.is-active')).toHaveText('1')
  await dialog.getByRole('button', { name: '获取模型列表', exact: true }).click()
  await expect(page.locator('.el-message', { hasText: '已获取 50 个模型，保存连接后可选用' })).toBeVisible()
  // 105 + 50 = 155 个模型：新增的 extra-00 在第 105 位，自动翻到第 2 页可见
  await expect(pager.locator('li.is-active')).toHaveText('2')
  await expect(dialog.locator('.connection-models')).toContainText('extra-00')
  await dialog.getByLabel('新增模型 ID', { exact: true }).fill('zzz-manual')
  await dialog.getByRole('button', { name: '添加模型', exact: true }).click()
  await expect(dialog.locator('.connection-models')).toContainText('zzz-manual')
  await expect(pager.locator('li.is-active')).toHaveText('2')
  await dialog.getByLabel('搜索模型', { exact: true }).fill('batch')
  await expect(pager.locator('li.is-active')).toHaveText('1')
  await expect(dialog.locator('.connection-models')).toContainText('batch-00')
  await dialog.getByLabel('搜索模型', { exact: true }).fill('')
  await expect(pager.locator('li.is-active')).toHaveText('1')
  await pager.getByText('2', { exact: true }).click()
  await expect(dialog.locator('.connection-models')).toContainText('extra-00')
  await dialog.getByRole('button', { name: '移除模型 zzz-manual', exact: true }).click()
  await expect(pager.locator('li.is-active')).toHaveText('2')
  await expect(dialog.locator('.connection-models')).toContainText('extra-00')
  expect(state.writes).toEqual([])
})

test('footer 校验错误与测试成功都有可见 toast，切换模板清旧错误与筛选', async ({ page }) => {
  test.slow()
  const { dialog } = await openConnectionManager(page, { create: true })
  await dialog.getByLabel('新增模型 ID', { exact: true }).fill('test-model')
  await dialog.getByRole('button', { name: '添加模型', exact: true }).click()
  await dialog.getByLabel('连接名称', { exact: true }).fill('草稿连接')
  await dialog.getByLabel('API 地址', { exact: true }).fill('https://draft.example.test/v1')
  await dialog.getByRole('button', { name: '测试默认模型', exact: true }).click()
  await expect(page.locator('.el-message', { hasText: '模型调用成功 · test-model · 12 ms' })).toBeVisible()
  await expect(dialog.getByRole('alert')).toContainText('模型调用成功 · test-model · 12 ms')
  await dialog.getByLabel('连接名称', { exact: true }).fill('')
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  await expect(page.locator('.el-message', { hasText: '请填写连接名称' })).toBeVisible()
  await expect(dialog.getByRole('alert')).toContainText('请填写连接名称')
  await dialog.getByLabel('搜索模型', { exact: true }).fill('zzz')
  await dialog.getByLabel('新增模型 ID', { exact: true }).fill('pending-model')
  await dialog.getByRole('combobox', { name: '服务商模板', exact: true }).click()
  await page.getByRole('option', { name: 'DeepSeek', exact: true }).click()
  await expect(dialog.getByRole('alert')).toHaveCount(0)
  await expect(dialog.getByLabel('搜索模型', { exact: true })).toHaveValue('')
  await expect(dialog.getByLabel('新增模型 ID', { exact: true })).toHaveValue('')
  await expect(dialog.locator('.connection-models')).toContainText('deepseek-flash')
})

test('请求头状态跟随所在行：删行后明文状态与值不串行，payload 只提交 name/value', async ({ page }) => {
  test.slow()
  const { state, dialog } = await openConnectionManager(page)
  await dialog.getByRole('button', { name: '高级连接选项', exact: true }).click()
  await expect(dialog.getByLabel('请求头 1 名称', { exact: true })).toHaveValue('X-Saved')
  await dialog.getByRole('button', { name: '添加请求头', exact: true }).click()
  await dialog.getByRole('button', { name: '添加请求头', exact: true }).click()
  await dialog.getByLabel('请求头 2 名称', { exact: true }).fill('X-Test-One')
  await dialog.getByLabel('请求头 2 内容', { exact: true }).fill('v1')
  await dialog.getByLabel('请求头 3 名称', { exact: true }).fill('X-Test-Two')
  await dialog.getByLabel('请求头 3 内容', { exact: true }).fill('v2')
  const rowThree = dialog.locator('.connection-header-row:has(input[aria-label="请求头 3 名称"])')
  // 密码框有值后才出现明文切换图标；点击后本行的内容输入框切为明文
  await rowThree.locator('.el-input__icon.el-input__password').click()
  await expect(dialog.getByLabel('请求头 3 内容', { exact: true })).toHaveAttribute('type', 'text')
  await dialog.locator('.connection-header-row').first().getByRole('button', { name: '移除', exact: true }).click()
  await expect(dialog.getByLabel('请求头 1 名称', { exact: true })).toHaveValue('X-Test-One')
  await expect(dialog.getByLabel('请求头 1 内容', { exact: true })).toHaveAttribute('type', 'password')
  // X-Test-Two 行自己的组件实例未被复用：值与明文状态跟随行保留
  await expect(dialog.getByLabel('请求头 2 名称', { exact: true })).toHaveValue('X-Test-Two')
  await expect(dialog.getByLabel('请求头 2 内容', { exact: true })).toHaveValue('v2')
  await expect(dialog.getByLabel('请求头 2 内容', { exact: true })).toHaveAttribute('type', 'text')
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  await expect(page.locator('.el-message', { hasText: '连接已保存' })).toBeVisible()
  const saved = state.writes.find(write => write.method === 'PUT' && write.path.endsWith('/connections/primary'))
  expect(saved.body.headers).toEqual({ 'X-Test-One': 'v1', 'X-Test-Two': 'v2' })
})

// —— 群列表 ——
async function setupGroups(page, { width = 1366 } = {}) {
  await page.setViewportSize({ width, height: 844 })
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    if (path === '/api/groups') return route.fulfill({ json: [{
      groupOpenId: 'group-1', name: '东楼测试群', addedAt: '2026-11-01T09:30:00Z',
      config: { isEnabled: true, triggerMode: 0, aiProactiveEnabled: true, botMentionOpenId: 'abc123def456' }
    }] })
    if (path === '/api/groups/quotas') return route.fulfill({ json: [] })
    if (path === '/api/config/characters') return route.fulfill({ json: [] })
    if (path === '/api/proactive/groups/group-1/memory') return route.fulfill({ json: { summary: '合成群记忆摘要。', updatedAt: '2026-11-12T10:00:00Z' } })
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${path}` } })
  })
  await page.goto('/#/groups')
  await expect(page.getByText('东楼测试群', { exact: true })).toBeVisible()
}

test('群表情设置与图库可在窄屏使用', async ({ page }) => {
  await setupGroups(page, { width: 390 })
  const photo = 'a'.repeat(64)
  const root = '/api/groups/group-1/stickers'
  let updated, config
  await page.route(`**${root}**`, async route => {
    const path = new URL(route.request().url()).pathname
    if (path === root) return route.fulfill({ json: [{ hash: photo, label: '', seenCount: 2, sentCount: 0, isEnabled: true }] })
    if (path.endsWith('/image')) return route.fulfill({
      contentType: 'image/png',
      body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aI1sAAAAASUVORK5CYII=', 'base64')
    })
    if (route.request().method() === 'PUT') updated = route.request().postDataJSON()
    return route.fulfill({ status: 204 })
  })
  await page.route('**/api/groups/group-1/config', async route => {
    config = route.request().postDataJSON()
    return route.fulfill({ json: { message: '已保存' } })
  })
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  for (const name of ['自动收藏表情', '回复使用表情']) {
    await page.locator('.el-form-item').filter({ hasText: name }).locator('.el-switch').click()
    await expect(page.getByRole('switch', { name, exact: true })).toHaveAttribute('aria-checked', 'true')
  }
  await page.getByRole('button', { name: '查看本群图库' }).click()
  const gallery = page.getByRole('dialog', { name: '本群表情包' })
  await expect(gallery).toBeVisible()
  await expect(gallery.getByRole('img', { name: '未命名表情' })).toBeVisible()
  await gallery.getByLabel('表情描述').fill('开心鼓掌')
  await gallery.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => updated).toEqual({ label: '开心鼓掌', isEnabled: true })
  await gallery.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(gallery).toBeHidden()
  await page.getByRole('dialog').getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => config).toMatchObject({ autoCollectStickers: true, useStickersInReplies: true })
})

test('群列表窄屏隐藏加入时间、保留身份列与固定操作列，@ 识别码帮助按钮同行', async ({ page }) => {
  await setupGroups(page)
  await expect(page.getByText('加入时间', { exact: true })).toBeVisible()
  const edit = page.getByRole('button', { name: '编辑', exact: true })
  await expect(edit).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByText('加入时间', { exact: true })).toHaveCount(0)
  await expect(page.getByText('群 OpenId', { exact: true })).toBeVisible()
  await expect(page.locator('code', { hasText: 'group-1' })).toBeVisible()
  await expect(edit).toBeVisible()
  const box = await edit.boundingBox()
  expect(box.x).toBeGreaterThanOrEqual(0)
  expect(box.x + box.width).toBeLessThanOrEqual(390)
  await edit.click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('群配置')
  await expect(dialog.getByText('上次更新', { exact: false })).toBeVisible()
  const item = dialog.locator('.el-form-item').filter({ hasText: '@ 识别码' })
  const input = await item.locator('input').boundingBox()
  const help = await item.locator('button.q').boundingBox()
  // 帮助按钮与输入框同一行（垂直居中）：换行时中心点会相差一个行高
  expect(Math.abs((help.y + help.height / 2) - (input.y + input.height / 2))).toBeLessThanOrEqual(6)
  expect(help.x).toBeGreaterThanOrEqual(input.x)
  expect(help.x + help.width).toBeLessThanOrEqual(390)
})

// —— 会话管理 ——
async function setupAccount(page, { width = 1366 } = {}) {
  await page.setViewportSize({ width, height: 844 })
  const state = {
    sessions: [
      { tokenPrefix: 'cur', remoteIp: '127.0.0.1', createdAt: '2026-11-10T08:00:00Z', lastSeenAt: '2026-11-14T08:00:00Z', isCurrent: true },
      { tokenPrefix: 'old', remoteIp: '192.0.2.8', createdAt: '2026-11-01T08:00:00Z', lastSeenAt: '2026-11-13T08:00:00Z', isCurrent: false }
    ],
    revokes: []
  }
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname, method = req.method()
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    if (path === '/api/auth/sessions' && method === 'GET') return route.fulfill({ json: state.sessions })
    if (path === '/api/auth/sessions/revoke' && method === 'POST') {
      const tokenPrefix = req.postDataJSON().tokenPrefix
      state.revokes.push(tokenPrefix)
      state.sessions = state.sessions.filter(session => session.tokenPrefix !== tokenPrefix)
      return route.fulfill({ json: {} })
    }
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${method} ${path}` } })
  })
  await page.goto('/#/account')
  await expect(page.locator('code', { hasText: 'cur' })).toBeVisible()
  return state
}

test('注销会话必须确认，窄屏隐藏创建时间并保留最后活跃与固定操作列', async ({ page }) => {
  const state = await setupAccount(page)
  await expect(page.getByText('创建时间', { exact: true })).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByText('创建时间', { exact: true })).toHaveCount(0)
  await expect(page.getByText('最后活跃', { exact: true })).toBeVisible()
  const revoke = page.getByRole('button', { name: '注销', exact: true })
  await expect(revoke).toBeVisible()
  const box = await revoke.boundingBox()
  expect(box.x).toBeGreaterThanOrEqual(0)
  expect(box.x + box.width).toBeLessThanOrEqual(390)
  const confirmation = page.locator('.el-message-box')
  await revoke.click()
  await expect(confirmation).toBeVisible()
  await expect(confirmation).toContainText('确定注销该会话')
  await confirmation.getByRole('button', { name: '取消', exact: true }).click()
  await expect(confirmation).toBeHidden()
  expect(state.revokes).toEqual([])
  await revoke.click()
  await expect(confirmation).toBeVisible()
  await confirmation.getByRole('button', { name: '注销', exact: true }).click()
  await expect(page.locator('.el-message', { hasText: '会话已注销' })).toBeVisible()
  expect(state.revokes).toEqual(['old'])
  await expect(page.locator('code', { hasText: 'old' })).toHaveCount(0)
})

// —— AI 统计 ——
async function setupStats(page, usage, { width = 1366 } = {}) {
  await page.setViewportSize({ width, height: 844 })
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    if (path === '/api/chat/usage') return route.fulfill({ json: usage })
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${path}` } })
  })
  await page.goto('/#/ai-stats')
}

test('统计无数据时不显示 Top 0', async ({ page }) => {
  await setupStats(page, { daily: [], groups: [], users: [], summary: { callCount: 0, failedCount: 0, unknownUsageCount: 0 } })
  await expect(page.getByText('群 Token 排行', { exact: true })).toBeVisible()
  await expect(page.getByText('提问用户 Token 排行', { exact: true })).toBeVisible()
  await expect(page.getByText(/Top \d/)).toHaveCount(0)
})

test('排行显示 Top N，窄屏保持表头同行、页面不横向溢出', async ({ page }) => {
  const usage = {
    daily: [{ date: '2026-11-13', totalTokens: 5432, callCount: 12 }],
    groups: [
      { groupId: 'g1', groupName: '东楼一群', callCount: 8, totalTokens: 4000 },
      { groupId: 'g2', groupName: '西楼二群', callCount: 3, totalTokens: 1000 },
      { groupId: 'g3', groupName: '北楼三群', callCount: 1, totalTokens: 432 }
    ],
    users: [
      { userId: 'u1', userName: '提问用户甲', callCount: 5, totalTokens: 3000 },
      { userId: 'u2', userName: '提问用户乙', callCount: 7, totalTokens: 2432 }
    ],
    summary: { callCount: 12, failedCount: 0, unknownUsageCount: 0 }
  }
  await setupStats(page, usage)
  const groupsCard = page.locator('.el-card').filter({ hasText: '群 Token 排行' })
  const usersCard = page.locator('.el-card').filter({ hasText: '提问用户 Token 排行' })
  await expect(groupsCard.locator('.el-card__header')).toContainText('（Top 3）')
  await expect(usersCard.locator('.el-card__header')).toContainText('（Top 2）')
  await expect(groupsCard).toContainText('东楼一群')
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await expect(groupsCard).toContainText('东楼一群')
  await expect(usersCard).toContainText('提问用户甲')
  for (const card of [groupsCard, usersCard]) {
    await expect.poll(async () => {
      const tops = await card.locator('th').evaluateAll(els => els.map(el => el.getBoundingClientRect().top))
      return Math.max(...tops) - Math.min(...tops)
    }).toBeLessThanOrEqual(2)
  }
})
