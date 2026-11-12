import { test, expect } from '@playwright/test'
import { normalizeAiSettings } from '../src/aiSettings.js'

async function setup(page, { failLoad = false, initialTemperature = 0.7 } = {}) {
  const state = {
    config: normalizeAiSettings({
      apiBaseUrl: 'https://model.example.test/v1', apiKey: 'sk-****', modelName: 'daily-model', personality: 'helper',
      defaultSystemPrompt: '内置提示词示例', webSearchEnabled: true, temperature: initialTemperature,
      availableProviders: [{ id: 'example', name: '示例服务商', apiBaseUrl: 'https://other.example.test/v1', defaultModel: 'other-model', keyHelpText: '服务商提供的密钥' }],
      availableCharacters: [{ name: 'helper', displayName: '默认助手', isCustom: false, description: '内置人设', systemPrompt: '友好回答' }],
      externalSearch: { baseUrl: 'https://search.example.test', apiKey: 'search-****' }
    }),
    presets: [{ id: 'daily', name: '日常配置', description: '日常对话', modelName: 'preset-model', apiBaseUrl: 'https://model.example.test/v1', isValid: true, hasApiKey: false, updatedAt: '2026-11-11T00:00:00Z' }],
    writes: [], probes: [], failSave: false, failLoad
  }
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname, method = req.method()
    const body = () => req.postDataJSON()
    if (path === '/api/config/ai') {
      if (method === 'GET') {
        if (state.failLoad) return route.fulfill({ status: 503, json: { error: '测试：读取失败' } })
        return route.fulfill({ json: state.config })
      }
      if (method === 'PUT') {
        state.writes.push({ path, body: body() })
        if (state.failSave) return route.fulfill({ status: 503, json: { error: '测试：保存失败' } })
        state.config = { ...state.config, ...body() }
        return route.fulfill({ json: { message: '已保存' } })
      }
    }
    if (path === '/api/config/ai/presets') {
      if (method === 'GET') return route.fulfill({ json: state.presets })
      state.writes.push({ path, body: body() })
      const added = { ...state.presets[0], id: 'new-preset', name: body().name, description: body().description, hasApiKey: body().includeApiKey }
      state.presets.push(added)
      return route.fulfill({ json: added })
    }
    if (path.endsWith('/daily/apply')) {
      state.writes.push({ path })
      state.config = { ...state.config, modelName: 'preset-model' }
      return route.fulfill({ json: state.config })
    }
    if (path === '/api/config/ai/presets/daily' && method === 'PUT') {
      state.writes.push({ path, body: body() })
      Object.assign(state.presets[0], body(), body().config ? { modelName: body().config.modelName } : {})
      return route.fulfill({ json: state.presets[0] })
    }
    if (path.endsWith('/daily/duplicate')) {
      const copy = { ...state.presets[0], id: 'daily-copy', name: `${state.presets[0].name}副本` }
      state.presets.push(copy)
      return route.fulfill({ json: copy })
    }
    if (path.endsWith('/daily-copy') && method === 'DELETE') {
      state.presets = state.presets.filter(preset => preset.id !== 'daily-copy')
      return route.fulfill({ json: {} })
    }
    if (path === '/api/config/ai/probe-search') {
      state.probes.push(body())
      state.config.externalSearchStatus = { verifiedAt: '2026-11-11T01:00:00Z' }
      return route.fulfill({ json: { verdict: 'verified', detail: '测试成功', hits: [{ title: '合成检索结果', url: 'https://result.example.test' }] } })
    }
    if (path === '/api/config/ai/probe-websearch/stream') {
      state.probes.push(body())
      return route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'done', report: { reachable: true, basePromptTokens: 12, results: [{ shape: 'enable_search', description: '示例策略', status: 'verified', imageStatus: 'not_tested', note: '合成验证结果' }] } }) + '\n' })
    }
    return route.fulfill({ json: path === '/api/status' ? { isConnected: false } : [] })
  })
  await page.goto('/#/ai-settings')
  if (!failLoad) await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('daily-model')
  return state
}

test('服务商填入、零值、草稿保存和重新加载', async ({ page }) => {
  const state = await setup(page)
  await page.locator('.el-select').filter({ has: page.getByLabel('服务商', { exact: true }) }).click()
  await page.getByRole('option', { name: /示例服务商/ }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('other-model')
  await expect(page.locator('.ai-settings-form').getByLabel('API Key', { exact: true })).toHaveValue('sk-****')
  await page.getByLabel('回复随机度', { exact: true }).fill('0')
  await page.getByLabel('附图张数', { exact: true }).fill('0')
  await page.getByLabel('群记忆用于回复', { exact: true }).locator('..').click()
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
  expect(state.writes[0].body).toMatchObject({ modelName: 'other-model', apiKey: 'sk-****', temperature: 0, maxAttachedImages: 0, groupMemoryInjectionEnabled: false })
  expect(state.writes[0].body).not.toHaveProperty('availableProviders')
  await page.getByLabel('模型名称', { exact: true }).fill('draft-model')
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '继续编辑', exact: true }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('draft-model')
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '放弃修改', exact: true }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('other-model')
})

test('界面精度调整后显示待保存，允许确认并保存调整值', async ({ page }) => {
  const state = await setup(page, { initialTemperature: 0.75 })
  await expect(page.getByLabel('回复随机度', { exact: true })).toHaveValue('0.8')
  await expect(page.locator('.ai-save-status')).toHaveText('有修改未保存')
  await expect(page.getByRole('button', { name: '保存配置', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '继续编辑', exact: true }).click()
  expect(state.config.temperature).toBe(0.75)
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
  expect(state.writes[0].body.temperature).toBe(0.8)
})

test('读取失败阻止空表单覆盖，保存失败保留草稿', async ({ page }) => {
  const state = await setup(page, { failLoad: true })
  await expect(page.locator('.ai-load-error')).toBeVisible()
  await expect(page.getByLabel('模型名称', { exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: '保存配置', exact: true })).toBeDisabled()
  state.failLoad = false
  await page.getByRole('button', { name: '重新读取', exact: true }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toBeEnabled()
  await page.getByLabel('模型名称', { exact: true }).fill('keep-draft')
  state.failSave = true
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.getByText('测试：保存失败', { exact: true })).toBeVisible()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('keep-draft')
  await expect(page.locator('.ai-save-status')).toHaveText('有修改未保存')
  state.failSave = false
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
})

test('预算冲突自动展开，空数字不允许保存', async ({ page }) => {
  const state = await setup(page)
  await page.getByLabel('回复随机度', { exact: true }).fill('')
  await page.getByLabel('模型名称', { exact: true }).click()
  await expect(page.locator('.ai-form-error')).toContainText('回复随机度')
  await expect(page.getByRole('button', { name: '保存配置', exact: true })).toBeDisabled()
  await page.getByLabel('回复随机度', { exact: true }).fill('0.7')
  await page.getByLabel('回复输出上限', { exact: true }).fill('32768')
  await page.getByLabel('模型名称', { exact: true }).click()
  await expect(page.getByLabel('输入预算', { exact: true })).toBeVisible()
  await expect(page.locator('.ai-form-error')).toContainText('模型上下文容量不足')
  expect(state.writes).toHaveLength(0)
})

test('保存预设记录草稿；应用预设确认后立即保存', async ({ page }) => {
  const state = await setup(page)
  await page.getByLabel('模型名称', { exact: true }).fill('snapshot-model')
  await page.getByRole('button', { name: '存为预设', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '保存为预设', exact: true })
  await dialog.getByLabel('名称', { exact: true }).fill('我的配置')
  await dialog.getByRole('switch').locator('..').click()
  await dialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(dialog).toBeHidden()
  expect(state.writes).toHaveLength(1)
  expect(state.writes[0]).toMatchObject({ path: '/api/config/ai/presets', body: { name: '我的配置', includeApiKey: false, config: { modelName: 'snapshot-model' } } })
  await expect(page.locator('.ai-save-status')).toHaveText('有修改未保存')
  await page.locator('.el-select').filter({ has: page.getByLabel('配置预设', { exact: true }) }).click()
  await page.getByRole('option', { name: /日常配置/ }).click()
  await page.locator('.ai-preset-toolbar').getByRole('button', { name: '应用并保存', exact: true }).click()
  await expect(page.locator('.el-message-box')).toContainText('未保存修改')
  await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
  expect(state.writes).toHaveLength(1)
  await page.locator('.ai-preset-toolbar').getByRole('button', { name: '应用并保存', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '应用并保存', exact: true }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('preset-model')
  await expect(page.locator('.ai-settings-form').getByLabel('API Key', { exact: true })).toHaveValue('sk-****')
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
})

test('预设库的编辑、覆盖、复制和删除保留各自的保存范围', async ({ page }) => {
  const state = await setup(page)
  await page.getByLabel('模型名称', { exact: true }).fill('library-draft')
  await page.getByRole('button', { name: '预设库', exact: true }).click()
  const library = page.getByRole('dialog', { name: 'AI 配置预设', exact: true })
  const item = library.locator('article').first()
  const command = async name => {
    await item.getByRole('button', { name: /更多操作/ }).click()
    const menuItem = page.getByRole('menuitem', { name, exact: true })
    await menuItem.click()
    await expect(menuItem).toBeHidden()
  }
  await command('编辑名称与备注')
  const edit = page.getByRole('dialog', { name: '编辑预设', exact: true })
  await edit.getByLabel('名称', { exact: true }).fill('改名后的预设')
  await edit.getByLabel('备注', { exact: true }).fill('新的备注')
  await edit.getByRole('button', { name: '保存', exact: true }).click()
  await expect(edit).toBeHidden()
  await expect(item).toContainText('改名后的预设')
  await command('用当前表单覆盖')
  await expect(page.locator('.el-message-box')).toContainText('未保存修改')
  await page.locator('.el-message-box').getByRole('button', { name: '覆盖', exact: true }).click()
  await expect(item).toContainText('library-draft')
  expect(state.writes.at(-1).body.config.modelName).toBe('library-draft')
  expect(state.writes.every(write => write.path !== '/api/config/ai')).toBe(true)
  await command('复制预设')
  const copy = library.locator('article').filter({ hasText: '改名后的预设副本' })
  await expect(copy).toBeVisible()
  await copy.getByRole('button', { name: /更多操作/ }).click()
  await page.getByRole('menuitem', { name: '删除预设', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '删除', exact: true }).click()
  await expect(copy).toHaveCount(0)
  await library.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('library-draft')
  await expect(page.locator('.ai-save-status')).toHaveText('有修改未保存')
})

test('切换搜索通道保留参数；测试当前草稿不会保存或丢失修改', async ({ page }) => {
  const state = await setup(page)
  await page.getByLabel('模型名称', { exact: true }).fill('unsaved-model')
  await page.getByRole('radio', { name: '独立搜索服务', exact: true }).locator('..').click()
  await expect(page.getByLabel('搜索 API Key', { exact: true })).toBeDisabled()
  await page.locator('.el-select').filter({ has: page.getByLabel('搜索服务商', { exact: true }) }).click()
  await page.getByRole('option', { name: 'Tavily', exact: true }).click()
  await page.getByLabel('搜索 API Key', { exact: true }).fill('synthetic-search-key')
  await page.getByRole('radio', { name: '模型内建搜索', exact: true }).locator('..').click()
  await page.getByRole('radio', { name: '独立搜索服务', exact: true }).locator('..').click()
  await expect(page.getByLabel('搜索 API Key', { exact: true })).toHaveValue('synthetic-search-key')
  await page.getByRole('button', { name: '测试搜索', exact: true }).click()
  const report = page.getByRole('dialog', { name: '测试搜索 · 独立搜索服务', exact: true })
  await expect(report.getByRole('link', { name: /合成检索结果/ })).toBeVisible()
  await report.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(page.getByLabel('模型名称', { exact: true })).toHaveValue('unsaved-model')
  expect(state.probes[0]).toMatchObject({ provider: 'tavily', apiKey: 'synthetic-search-key', maxResults: 5 })
  expect(state.writes).toHaveLength(0)
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
  expect(state.writes[0].body.externalSearch).toMatchObject({ enabled: true, provider: 'tavily', apiKey: 'synthetic-search-key' })
})

test('内建搜索探测保留实测报告和选用保存功能', async ({ page }) => {
  const state = await setup(page)
  await page.getByRole('button', { name: '检测联网搜索', exact: true }).click()
  const report = page.getByRole('dialog', { name: '联网搜索探测报告', exact: true })
  await expect(report.getByText('合成验证结果', { exact: true })).toBeVisible()
  expect(state.writes).toHaveLength(0)
  await report.getByRole('button', { name: '选用', exact: true }).click()
  await expect(report.getByRole('button', { name: '已选用', exact: true })).toBeVisible()
  await expect.poll(() => state.writes.length).toBe(1)
  expect(state.writes[0].body).toMatchObject({ webSearchEnabled: true, webSearchShape: 'enable_search', webSearchEndpoint: 'openai' })
})

for (const darkMode of [false, true]) for (const width of [1440, 1024, 390, 320]) {
  test(`AI 设置布局：${width}px · ${darkMode ? '深色' : '浅色'}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1080 })
    await page.addInitScript(darkMode => localStorage.setItem('mutter-panel-settings', JSON.stringify({ primaryColor: '#4f46e5', darkMode, compact: false })), darkMode)
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    await setup(page)
    await page.getByRole('button', { name: /容量与输入预算/ }).click()
    await page.getByRole('radio', { name: '独立搜索服务', exact: true }).locator('..').click()
    await page.getByRole('button', { name: /检索与容错/ }).click()
    await expect(page.getByLabel('检索超时', { exact: true })).toBeVisible()
    expect(await page.locator('.ai-settings select').count()).toBe(0)
    expect(await page.locator('.ai-settings .el-input-number__increase, .ai-settings .el-input-number__decrease').count()).toBe(0)
    const overflowing = await page.locator('.ai-settings .el-card, .ai-settings .ai-field, .ai-settings .el-input, .ai-settings .el-radio-group, .ai-settings .el-switch').evaluateAll(elements => elements.filter(el => el.getBoundingClientRect().left < 0 || el.getBoundingClientRect().right > innerWidth).map(el => el.className))
    expect(overflowing).toEqual([])
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    for (const [button, title, cap] of [['预设库', 'AI 配置预设', 760], ['存为预设', '保存为预设', 520], ['查看内置默认提示词', '内置默认系统提示词', 760]]) {
      await page.getByRole('button', { name: button, exact: true }).click()
      const dialog = page.getByRole('dialog', { name: title, exact: true })
      await expect(dialog).toBeVisible()
      await expect.poll(async () => Math.abs((await dialog.locator('.el-dialog').boundingBox()).width - Math.min(cap, width - 32))).toBeLessThanOrEqual(1)
      expect(await dialog.locator('.el-dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
      await dialog.locator('.el-dialog__headerbtn').click()
      await expect(dialog).toBeHidden()
    }
    expect(errors).toEqual([])
    if (process.env.MUTTER_UI_SCREENSHOTS) {
      await page.setViewportSize({ width, height: 3200 })
      await page.locator('.panel-main').evaluate(el => el.scrollTop = 0)
      await page.screenshot({ path: `../artifacts/ai-settings/${width}-${darkMode ? 'dark' : 'light'}.png`, fullPage: true })
    }
  })
}
