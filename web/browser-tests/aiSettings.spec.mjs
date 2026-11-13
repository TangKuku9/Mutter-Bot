import { test, expect } from '@playwright/test'
import { normalizeAiSettings } from '../src/aiSettings.js'

async function setup(page, { failLoad = false, initialTemperature = 0.7, emptyConnections = false } = {}) {
  const state = {
    config: normalizeAiSettings({
      providerConnectionId: 'primary',
      connections: [
        { id: 'primary', name: '主账号', protocol: 'openai', apiBaseUrl: 'https://model.example.test/v1', apiKey: '********', hasApiKey: true, defaultModel: 'daily-model', timeoutSeconds: 60,
          models: ['daily-model', 'draft-model', 'keep-draft', 'snapshot-model', 'preset-model', 'library-draft', 'unsaved-model'].map(id => ({ id, name: id, isManual: true })) },
        { id: 'alternate', name: '示例服务商', protocol: 'openai', apiBaseUrl: 'https://other.example.test/v1', apiKey: '********', hasApiKey: true, defaultModel: 'other-model', timeoutSeconds: 90,
          models: ['other-model', 'draft-model'].map(id => ({ id, name: id, isManual: true })) }
      ],
      apiBaseUrl: 'https://model.example.test/v1', apiKey: 'sk-****', modelName: 'daily-model', personality: 'helper',
      defaultSystemPrompt: '内置提示词示例', webSearchEnabled: true, temperature: initialTemperature,
      availableProviders: [
        { id: 'example', name: '示例服务商', category: 'china', apiBaseUrl: 'https://other.example.test/v1', defaultModel: 'other-model', keyHelpText: '服务商提供的密钥' },
        { id: 'deepseek', name: 'DeepSeek', category: 'china', protocol: 'openai', apiBaseUrl: 'https://api.deepseek.com/v1', defaultModel: 'deepseek-flash', models: ['deepseek-flash', 'deepseek-v4-pro'], modelsUpdatedAt: '2026-11-13', documentationUrl: 'https://api-docs.deepseek.com/quick_start/pricing/' },
        { id: 'gemini', name: 'Google Gemini', category: 'international', protocol: 'gemini', apiBaseUrl: 'https://generativelanguage.googleapis.com/v1beta', defaultModel: 'gemini-4-hydrogen', models: ['gemini-4-hydrogen', 'gemini-4-argon'], modelsUpdatedAt: '2026-11-13' },
        { id: 'custom', name: '自定义服务商', category: 'custom', models: [] }
      ],
      availableCharacters: [{ name: 'helper', displayName: '默认助手', isCustom: false, description: '内置人设', systemPrompt: '友好回答' }],
      externalSearch: { baseUrl: 'https://search.example.test', apiKey: 'search-****' }
    }),
    presets: [{ id: 'daily', providerConnectionId: 'primary', name: '日常配置', description: '日常对话', modelName: 'preset-model', apiBaseUrl: 'https://model.example.test/v1', isValid: true, hasApiKey: false, updatedAt: '2026-11-11T00:00:00Z' }],
    writes: [], probes: [], failSave: false, failDiscover: false, failLoad, allowSystemProxy: false
  }
  if (emptyConnections) Object.assign(state.config, { connections: [], providerConnectionId: '', modelName: '', apiBaseUrl: '', apiKey: '' })
  for (const connection of state.config.connections) connection.updatedAt = '2026-11-13T00:00:00.000Z'
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const req = route.request(), path = new URL(req.url()).pathname, method = req.method()
    const body = () => req.postDataJSON()
    if (path === '/api/config/ai/connections') {
      if (method === 'GET') return route.fulfill({ json: state.config.connections })
      state.writes.push({ path, body: body() })
      const added = { ...body(), id: `connection-${state.config.connections.length}`, updatedAt: '2026-11-13T00:00:00.000Z', hasApiKey: !!body().apiKey, apiKey: body().apiKey ? '********' : '' }
      state.config.connections.push(added)
      return route.fulfill({ json: added })
    }
    if (path === '/api/config/ai/connections/discover') {
      if (state.failDiscover) return route.fulfill({ status: 502, json: { error: '测试：模型列表获取失败，已有模型已保留' } })
      return route.fulfill({ json: [{ id: 'discovered-model', name: 'discovered-model', isManual: false }] })
    }
    if (path.startsWith('/api/config/ai/connections/') && method === 'PUT') {
      state.writes.push({ path, body: body() })
      const existing = state.config.connections.find(c => c.id === path.split('/').at(-1))
      if (body().updatedAt !== existing.updatedAt) return route.fulfill({ status: 409, json: { error: '连接已被其他会话修改，请重新读取连接后再保存' } })
      const updatedAt = new Date(new Date(existing.updatedAt).getTime() + 1).toISOString()
      Object.assign(existing, body(), { updatedAt, apiKey: body().apiKey ? '********' : '', hasApiKey: !!body().apiKey })
      return route.fulfill({ json: existing })
    }
    if (path === '/api/config/ai/connections/test') return route.fulfill({ json: { message: '模型调用成功', model: body().defaultModel, elapsedMs: 12 } })
    if (path === '/api/config/network') {
      if (method === 'GET') return route.fulfill({ json: { allowSystemProxy: state.allowSystemProxy } })
      state.writes.push({ path, body: body() })
      state.allowSystemProxy = !!body().allowSystemProxy
      return route.fulfill({ status: 204, body: '' })
    }
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
    if (path === '/api/config/ai/presets/daily' && method === 'GET') return route.fulfill({ json: { ...state.presets[0], config: { ...state.config, modelName: state.presets[0].modelName, webSearchEndpoint: 'openai', webSearchShape: 'enable_search' } } })
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
    if (path === '/api/config/characters' && method === 'GET') return route.fulfill({ json: [] })
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    // 未列出的路径必须显式失败，避免 200 空响应掩盖错误端点或漏掉的 mock
    return route.fulfill({ status: 404, json: { error: `测试：未处理的 API 请求 ${method} ${path}` } })
  })
  await page.goto('/#/ai-settings')
  if (!failLoad && !emptyConnections) await expectModel(page, 'daily-model')
  return state
}

const connectionSelect = page => page.getByTestId('connection-select')
const modelSelect = page => page.getByTestId('model-select')
async function expectModel(page, id) { await expect(modelSelect(page)).toContainText(id) }
async function selectModel(page, id) {
  await modelSelect(page).click()
  await page.getByRole('option', { name: new RegExp(id) }).click()
}

test('自定义服务商保存、模型获取失败保留手动目录、选用与测试', async ({ page }) => {
  const state = await setup(page)
  await page.getByRole('button', { name: '添加服务商', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
  await dialog.getByLabel('连接名称', { exact: true }).fill('自建中转')
  await dialog.getByLabel('API 地址', { exact: true }).fill('https://custom.example.test/v1')
  await dialog.getByLabel('API Key', { exact: true }).fill('synthetic-key')
  await dialog.getByLabel('新增模型 ID', { exact: true }).fill('manual-model')
  await dialog.getByRole('button', { name: '添加模型', exact: true }).click()
  state.failDiscover = true
  await dialog.getByRole('button', { name: '获取模型列表', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('已有模型已保留')
  await expect(dialog.locator('.connection-models')).toContainText('manual-model')
  state.failDiscover = false
  await dialog.getByRole('button', { name: '获取模型列表', exact: true }).click()
  await expect(dialog.locator('.connection-models')).toContainText('discovered-model')
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  await expect(dialog.locator('.connection-list')).toContainText('自建中转')
  expect(state.writes.filter(w => w.path === '/api/config/ai')).toHaveLength(0)
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await connectionSelect(page).click()
  await page.getByRole('option', { name: /自建中转/ }).click()
  await selectModel(page, 'discovered-model')
  await page.getByRole('button', { name: '测试当前模型', exact: true }).click()
  await expect(page.locator('[data-section="connection"]')).toContainText('模型调用成功')
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  expect(state.writes.at(-1).body).toMatchObject({ providerConnectionId: 'connection-2', modelName: 'discovered-model', apiKey: '' })
})

test('首次添加无密钥本地连接会填入主配置，仍需明确保存生效', async ({ page }) => {
  const state = await setup(page, { emptyConnections: true })
  await page.getByRole('button', { name: '添加服务商', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
  await dialog.getByLabel('连接名称', { exact: true }).fill('本机模型')
  await dialog.getByLabel('API 地址', { exact: true }).fill('http://127.0.0.1:1234/v1')
  await dialog.getByLabel('新增模型 ID', { exact: true }).fill('local-model')
  await dialog.getByRole('button', { name: '添加模型', exact: true }).click()
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  await expect(dialog.locator('.connection-list')).toContainText('本机模型')
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expectModel(page, 'local-model')
  expect(state.writes.filter(w => w.path === '/api/config/ai')).toHaveLength(0)
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  expect(state.writes.at(-1).body).toMatchObject({ providerConnectionId: 'connection-0', modelName: 'local-model', apiKey: '' })
})

test('中国与国际模板可搜索选用，Gemini 自动填入原生协议且不沿用上一模板密钥', async ({ page }) => {
  const state = await setup(page)
  await page.getByRole('button', { name: '添加服务商', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
  const template = dialog.getByRole('combobox', { name: '服务商模板', exact: true })
  await template.click()
  await expect(page.getByText('中国服务商', { exact: true })).toBeVisible()
  await expect(page.getByText('国际服务商', { exact: true })).toBeVisible()
  await page.getByRole('option', { name: 'DeepSeek', exact: true }).click()
  await expect(dialog.getByLabel('API 地址', { exact: true })).toHaveValue('https://api.deepseek.com/v1')
  await expect(dialog.locator('.connection-models')).toContainText('deepseek-flash')
  await expect(dialog.getByRole('link', { name: '官方模型文档' })).toHaveAttribute('href', 'https://api-docs.deepseek.com/quick_start/pricing/')
  await dialog.getByLabel('API Key', { exact: true }).fill('previous-template-key')
  await template.click()
  await page.getByRole('option', { name: 'Google Gemini', exact: true }).click()
  await expect(dialog.getByLabel('API Key', { exact: true })).toHaveValue('')
  await expect(dialog.getByLabel('API 地址', { exact: true })).toHaveValue('https://generativelanguage.googleapis.com/v1beta')
  await expect(dialog.locator('.connection-form')).toContainText('Gemini 原生')
  await dialog.getByLabel('API Key', { exact: true }).fill('gemini-test-key')
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  await expect(dialog.locator('.connection-list')).toContainText('Google Gemini')
  expect(state.writes.at(-1).body).toMatchObject({ templateId: 'gemini', protocol: 'gemini', defaultModel: 'gemini-4-hydrogen' })
})

test('旧连接可补充新内置模型，保留旧模型、显示名、默认模型与密钥', async ({ page }) => {
  const state = await setup(page)
  state.config.connections[0].templateId = 'deepseek'
  state.config.connections[0].models[0].name = '我的日常模型'
  await page.reload()
  await page.getByRole('button', { name: '管理服务商', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
  await dialog.getByRole('button', { name: '补充最新内置模型', exact: true }).click()
  await expect(dialog.locator('.connection-models')).toContainText('deepseek-flash')
  await expect(dialog.getByLabel('模型 daily-model 的显示名称', { exact: true })).toHaveValue('我的日常模型')
  await expect(dialog.getByLabel('API Key', { exact: true })).toHaveValue('********')
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  await expect(dialog.locator('.connection-editor-heading')).not.toContainText('有未保存修改')
  expect(state.writes.at(-1).body.defaultModel).toBe('daily-model')
  expect(state.writes.at(-1).body.models.map(model => model.id)).toContain('deepseek-v4-pro')
  expect(state.writes.at(-1).body.models.map(model => model.id)).toContain('daily-model')
  expect(state.writes.filter(w => w.path === '/api/config/ai')).toHaveLength(0)
})

test('连接编辑清理跨地址密钥，恢复原地址与默认超时后可保存', async ({ page }) => {
  const state = await setup(page)
  await page.getByRole('button', { name: '管理服务商', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
  const address = dialog.getByLabel('API 地址', { exact: true })
  const key = dialog.getByLabel('API Key', { exact: true })
  await expect(key).toHaveValue('********')
  await address.fill('https://other.example.test/v1')
  await expect(key).toHaveValue('')
  await expect(page.getByText('接口地址已更换，请重新填写 API Key 与自定义请求头')).toBeVisible()
  await address.fill('https://model.example.test/v1')
  await expect(key).toHaveValue('********')
  await dialog.getByRole('button', { name: '高级连接选项', exact: true }).click()
  const timeout = dialog.getByLabel('连接超时（秒）', { exact: true })
  await expect(timeout).toHaveValue('60')
  await timeout.fill('')
  await timeout.press('Tab')
  await expect(timeout).toHaveValue('60')
  await dialog.getByRole('button', { name: '保存连接', exact: true }).click()
  const saved = state.writes.filter(write => write.path === '/api/config/ai/connections/primary').at(-1)
  expect(saved.body.timeoutSeconds).toBe(60)
})

test('网络出口默认直连，开启系统代理后保存并回读', async ({ page }) => {
  const state = await setup(page)
  const toggle = page.getByRole('switch', { name: '允许使用系统代理', exact: true })
  const clickable = page.locator('.ai-network-outlet .el-switch')
  await expect(toggle).toHaveAttribute('aria-checked', 'false')
  await clickable.click()
  await expect(toggle).toHaveAttribute('aria-checked', 'true')
  await page.getByRole('button', { name: '保存网络设置', exact: true }).click()
  await expect(page.getByText('网络出口设置已保存')).toBeVisible()
  expect(state.writes.find(write => write.path === '/api/config/network').body).toEqual({ allowSystemProxy: true })
  expect(state.allowSystemProxy).toBe(true)
})

test('切回连接恢复上次模型，收藏按连接保存', async ({ page }) => {
  await setup(page)
  await selectModel(page, 'snapshot-model')
  await modelSelect(page).click()
  await page.getByRole('button', { name: '收藏 snapshot-model', exact: true }).click()
  await page.keyboard.press('Escape')
  const provider = connectionSelect(page)
  await provider.click()
  await page.getByRole('option', { name: /示例服务商/ }).click()
  await selectModel(page, 'draft-model')
  await provider.click()
  await page.getByRole('option', { name: /主账号/ }).click()
  await expectModel(page, 'snapshot-model')
  const preferences = await page.evaluate(() => JSON.parse(localStorage.getItem('mutter.ai-model-preferences.v1')))
  expect(preferences.favorites).toEqual([{ connectionId: 'primary', modelId: 'snapshot-model' }])
  expect(preferences.last).toMatchObject({ primary: 'snapshot-model', alternate: 'draft-model' })
})

test('编辑预设可以切换连接和模型，并清空旧搜索策略', async ({ page }) => {
  const state = await setup(page)
  await page.getByRole('button', { name: '预设库', exact: true }).click()
  const library = page.getByRole('dialog', { name: 'AI 配置预设', exact: true })
  await library.getByRole('button', { name: /更多操作/ }).click()
  await page.getByRole('menuitem', { name: '编辑预设', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '编辑预设', exact: true })
  await dialog.getByTestId('preset-edit-connection-select').click()
  await page.getByRole('option', { name: /示例服务商/ }).click()
  await expect(dialog.getByTestId('preset-edit-provider-model-summary')).toContainText('other-model')
  await dialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(dialog).toBeHidden()
  expect(state.writes.at(-1).body.config).toMatchObject({
    providerConnectionId: 'alternate', modelName: 'other-model', temperature: 0.7, personality: 'helper',
    webSearchEndpoint: '', webSearchShape: ''
  })
  expect(state.writes.every(w => w.path !== '/api/config/ai')).toBe(true)
})

for (const width of [390, 1440]) {
  test(`服务商管理在 ${width}px 下可编辑且无横向溢出`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await setup(page)
    await page.getByRole('button', { name: '管理服务商', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '管理服务商', exact: true })
    await expect(dialog.getByLabel('连接名称', { exact: true })).toHaveValue('主账号')
    expect(await dialog.locator('.el-dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    await expect(dialog.locator('.el-dialog')).toHaveCSS('opacity', '1')
    if (process.env.MUTTER_UI_SCREENSHOTS) await page.screenshot({ path: `../artifacts/ai-settings/connections-${width}.png`, animations: 'disabled' })
  })
}

test('服务商填入、零值、草稿保存和重新加载', async ({ page }) => {
  const state = await setup(page)
  await connectionSelect(page).click()
  await page.getByRole('option', { name: /示例服务商/ }).click()
  await expectModel(page, 'other-model')
  await expect(page.locator('.picker-summary')).toContainText('密钥已配置')
  await page.getByLabel('回复随机度', { exact: true }).fill('0')
  await page.getByLabel('附图张数', { exact: true }).fill('0')
  await page.getByLabel('群记忆用于回复', { exact: true }).locator('..').click()
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
  expect(state.writes[0].body).toMatchObject({ providerConnectionId: 'alternate', modelName: 'other-model', apiKey: '', temperature: 0, maxAttachedImages: 0, groupMemoryInjectionEnabled: false })
  expect(state.writes[0].body).not.toHaveProperty('availableProviders')
  await selectModel(page, 'draft-model')
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '继续编辑', exact: true }).click()
  await expectModel(page, 'draft-model')
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '放弃修改', exact: true }).click()
  await expectModel(page, 'other-model')
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
  await selectModel(page, 'keep-draft')
  state.failSave = true
  await page.getByRole('button', { name: '保存配置', exact: true }).click()
  await expect(page.getByText('测试：保存失败', { exact: true })).toBeVisible()
  await expectModel(page, 'keep-draft')
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
  await selectModel(page, 'snapshot-model')
  await page.getByRole('button', { name: '存为预设', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '保存为预设', exact: true })
  await dialog.getByLabel('名称', { exact: true }).fill('我的配置')
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
  await expectModel(page, 'preset-model')
  await expect(page.locator('.picker-summary')).toContainText('密钥已配置')
  await expect(page.locator('.ai-save-status')).toHaveText('当前设置已保存')
})

test('预设库的编辑、覆盖、复制和删除保留各自的保存范围', async ({ page }) => {
  const state = await setup(page)
  await selectModel(page, 'library-draft')
  await page.getByRole('button', { name: '预设库', exact: true }).click()
  const library = page.getByRole('dialog', { name: 'AI 配置预设', exact: true })
  const item = library.locator('article').first()
  const command = async name => {
    await item.getByRole('button', { name: /更多操作/ }).click()
    const menuItem = page.getByRole('menuitem', { name, exact: true })
    await menuItem.click()
    await expect(menuItem).toBeHidden()
  }
  await command('编辑预设')
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
  await expectModel(page, 'library-draft')
  await expect(page.locator('.ai-save-status')).toHaveText('有修改未保存')
})

test('切换搜索通道保留参数；测试当前草稿不会保存或丢失修改', async ({ page }) => {
  const state = await setup(page)
  await selectModel(page, 'unsaved-model')
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
  await expectModel(page, 'unsaved-model')
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

// 桌面/手机、明/暗色各保留代表尺寸，避免完整尺寸笛卡尔积重复同一流程。
for (const [width, darkMode] of [[1440, false], [390, false], [1440, true], [320, true]]) {
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
