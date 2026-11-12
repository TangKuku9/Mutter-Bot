import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { parse, compileScript } from 'vue/compiler-sfc'
import { ref, computed, watch } from 'vue'
import { aiSettingsError, normalizeAiSettings, sanitizeAiSettingsForUi, safeHttpUrl } from '../src/aiSettings.js'
import { tokenBudget } from '../src/tokenBudget.js'

test('older AI configurations receive defaults without replacing zero, false or masked keys', () => {
  const form = normalizeAiSettings({ temperature: 0, maxAttachedImages: 0, groupMemoryInjectionEnabled: false, apiKey: 'sk-****', externalSearch: { apiKey: 'search-****', enabled: true } })
  assert.equal(form.temperature, 0)
  assert.equal(form.maxAttachedImages, 0)
  assert.equal(form.groupMemoryInjectionEnabled, false)
  assert.equal(form.apiKey, 'sk-****')
  assert.equal(form.externalSearch.apiKey, 'search-****')
  assert.equal(form.externalSearch.maxResults, 5)
  assert.equal(form.externalSearch.enabled, true)
  assert.equal(form.maxInputTokens, 24576)
  assert.equal(aiSettingsError(form), '')
})

test('fresh defaults are independent, including nested search settings', () => {
  const first = normalizeAiSettings({ externalSearch: null })
  first.externalSearch.maxResults = 10
  assert.equal(normalizeAiSettings().externalSearch.maxResults, 5)
  assert.deepEqual(first.externalSearchStatus, { verifiedAt: null })
})

test('empty numeric controls must not silently save fallback values', () => {
  for (const key of ['temperature', 'maxTokens', 'maxContextMessages', 'maxAttachedImages', 'contextWindowTokens', 'maxInputTokens']) {
    for (const value of [undefined, null, NaN]) {
      const form = normalizeAiSettings({ [key]: value })
      assert.notEqual(aiSettingsError(form), '', `${key}: ${value}`)
    }
  }
})

test('integer limits and the server attachment ceiling are validated', () => {
  assert.match(aiSettingsError(normalizeAiSettings({ maxContextMessages: 2.5 })), /历史消息/)
  assert.match(aiSettingsError(normalizeAiSettings({ ceilingAttachedImages: 4, maxAttachedImages: 5 })), /附图/)
  assert.match(aiSettingsError(normalizeAiSettings({ maxTokens: 63 })), /回复输出/)
  assert.equal(aiSettingsError(normalizeAiSettings({ temperature: 2, maxAttachedImages: 10 })), '')
})

test('context allocations include output and safety reserve', () => {
  assert.match(aiSettingsError(normalizeAiSettings({ contextWindowTokens: 4096 })), /超出容量/)
  assert.equal(aiSettingsError(normalizeAiSettings({ contextWindowTokens: 4096, maxTokens: 2048, maxInputTokens: 1024 })), '')
})

test('external search limits are only validated while the channel is enabled (P1-2)', () => {
  // 关闭通道时字段不渲染：旧数据里的非法值不能阻塞整个 AI 配置的保存
  assert.equal(aiSettingsError(normalizeAiSettings({ externalSearch: { enabled: false, maxResults: null, timeoutSeconds: 31 } })), '')
  assert.equal(aiSettingsError(normalizeAiSettings({ externalSearch: { enabled: false, maxResults: 0, timeoutSeconds: 99 } })), '')
  // 开启时照常校验
  assert.match(aiSettingsError(normalizeAiSettings({ externalSearch: { enabled: true, maxResults: null } })), /检索结果/)
  assert.match(aiSettingsError(normalizeAiSettings({ externalSearch: { enabled: true, timeoutSeconds: 31 } })), /检索超时/)
})

test('missing external search object falls back to defaults instead of throwing', () => {
  const form = normalizeAiSettings()
  delete form.externalSearch
  assert.equal(aiSettingsError(form), '')
})

test('sanitize converges out-of-range legacy values into the UI range', () => {
  const form = normalizeAiSettings({
    temperature: 0.75, contextWindowTokens: 4096, maxTokens: 32768, maxInputTokens: 32768,
    maxContextMessages: 2.5, ceilingAttachedImages: 10, maxAttachedImages: 20,
    externalSearch: { enabled: false, maxResults: 99, timeoutSeconds: null, baseUrl: null, apiKey: null }
  })
  assert.equal(sanitizeAiSettingsForUi(form), true)
  assert.equal(form.temperature, 0.8)
  // 与服务端 NormalizeAndValidateAiConfigInput 同序：容量 → 输出(≤容量-1536) → 输入(512..容量-输出-1024)
  assert.equal(form.maxTokens, 2560)
  assert.equal(form.maxInputTokens, 512)
  assert.equal(form.maxContextMessages, 3)
  assert.equal(form.maxAttachedImages, 10)
  assert.equal(form.externalSearch.maxResults, 10)
  assert.equal(form.externalSearch.timeoutSeconds, 8)
  assert.equal(form.externalSearch.baseUrl, '')
  assert.equal(form.externalSearch.apiKey, '')
  // 收敛后的表单应能直接通过校验，不再出现「显示值与保存值不一致」
  assert.equal(aiSettingsError(form), '')
})

test('sanitize keeps already-valid forms untouched', () => {
  const form = normalizeAiSettings({ temperature: 0.7 })
  assert.equal(sanitizeAiSettingsForUi(form), false)
})

test('normalize tolerates null payloads and null string fields', () => {
  const form = normalizeAiSettings(null)
  assert.equal(form.apiBaseUrl, '')
  assert.equal(form.temperature, 0.7)
  assert.equal(form.externalSearch.maxResults, 5)
  const withNulls = normalizeAiSettings({ externalSearch: { baseUrl: null, apiKey: null, provider: null } })
  assert.equal(withNulls.externalSearch.baseUrl, '')
  assert.equal(withNulls.externalSearch.apiKey, '')
  assert.equal(withNulls.externalSearch.provider, 'searxng')
  assert.equal(withNulls.externalSearchStatus, withNulls.externalSearchStatus)
})

test('small model capacity yields an actionable error instead of an impossible input hint', () => {
  const budget = tokenBudget(normalizeAiSettings({ contextWindowTokens: 4096, maxTokens: 3000, maxInputTokens: 512 }))
  assert.equal(budget.availableInput, 72)
  assert.match(budget.error, /模型上下文容量不足/)
  assert.match(budget.error, /只剩 72 Token/)
  assert.match(budget.error, /请提高上下文容量或降低回复输出上限/)
})

test('safeHttpUrl only lets http/https through', () => {
  assert.equal(safeHttpUrl('https://result.example.test/a?q=1'), 'https://result.example.test/a?q=1')
  assert.equal(safeHttpUrl('http://result.example.test/'), 'http://result.example.test/')
  assert.equal(safeHttpUrl('javascript:alert(1)'), '')
  assert.equal(safeHttpUrl('data:text/html;base64,AAAA'), '')
  assert.equal(safeHttpUrl('vbscript:msgbox'), '')
  assert.equal(safeHttpUrl('not a url'), '')
  assert.equal(safeHttpUrl(''), '')
  assert.equal(safeHttpUrl(null), '')
  assert.equal(safeHttpUrl(undefined), '')
})

// ── 页面级用例：编译 AiSettings.vue 的真实 setup，只替换网络与 element-plus ──
const { descriptor } = parse(readFileSync(new URL('../src/views/AiSettings.vue', import.meta.url), 'utf8'))
const script = compileScript(descriptor, { id: 'ai-settings-state-test', genDefaultAs: 'TestComponent' })
const setupBody = script.content.slice(script.content.indexOf('const TestComponent ='))
const flush = () => new Promise(resolve => setImmediate(resolve))

async function createPage(t, { initialModel = 'base-model' } = {}) {
  const state = {
    config: normalizeAiSettings({ modelName: initialModel, availableProviders: [], availableCharacters: [] }),
    presets: []
  }
  const messages = []
  const messageApi = {}
  for (const kind of ['success', 'info', 'warning', 'error']) {
    messageApi[kind] = (text) => messages.push({ kind, text: String(text?.message ?? text) })
  }
  let failConfigGet = false
  let failSave = false
  const writes = []
  const api = {
    async get(url) {
      if (url === '/config/ai') {
        if (failConfigGet) { failConfigGet = false; throw Object.assign(new Error('503'), { response: { status: 503 } }) }
        return { data: state.config }
      }
      if (url === '/config/ai/presets') return { data: state.presets }
      if (url === '/config/characters') return { data: [] }
      throw new Error(`unexpected GET ${url}`)
    },
    async put(url, body) {
      if (url === '/config/ai') {
        if (failSave) { failSave = false; throw Object.assign(new Error('503'), { response: { status: 503 } }) }
        writes.push({ url, body })
        state.config = { ...state.config, ...body }
        return { data: {} }
      }
      throw new Error(`unexpected PUT ${url}`)
    },
    async post(url) { throw new Error(`unexpected POST ${url}`) },
    async delete(url) { throw new Error(`unexpected DELETE ${url}`) }
  }
  const mounts = [], unmounts = []
  const modules = {
    vue: { ref, computed, watch, onMounted: fn => mounts.push(fn), onBeforeUnmount: fn => unmounts.push(fn) },
    'element-plus': {
      ElMessage: messageApi,
      ElMessageBox: { confirm: async () => { throw new Error('cancel') } }
    },
    '@element-plus/icons-vue': new Proxy({}, { get: (_target, name) => ({ name: String(name) }) }),
    '../api': { default: api },
    '../tokenBudget': await import('../src/tokenBudget.js'),
    '../useUnsavedChanges': { useUnsavedChanges: () => async () => true },
    '../paramHelp': await import('../src/paramHelp.js'),
    '../components/ParamHelpButton.vue': { name: 'ParamHelpButton', render: () => null },
    '../components/ParamHelpDrawer.vue': { name: 'ParamHelpDrawer', render: () => null },
    '../components/AiSettingField.vue': { name: 'AiSettingField', render: () => null },
    '../aiSettings': await import('../src/aiSettings.js')
  }
  const imports = Object.values(script.imports)
  const component = new Function(...imports.map(binding => binding.local),
    `${setupBody}\nreturn TestComponent.setup({}, { expose() {} })`)(
    ...imports.map(binding => modules[binding.source]?.[binding.imported]))
  t.after(() => unmounts.forEach(fn => fn()))
  mounts.forEach(fn => fn())
  await flush()
  return { component, state, messages, writes, setConfigGetFailure: () => { failConfigGet = true }, setSaveFailure: () => { failSave = true } }
}

test('page harness mounts the real setup', async t => {
  const { component } = await createPage(t)
  assert.equal(component.ready.value, true)
  assert.equal(component.loadError.value, false)
  assert.equal(component.isDirty.value, false)
})

test('PUT 成功但回读失败后，表单不再永久假脏（P1-3）', async t => {
  const { component, state, messages, writes, setConfigGetFailure } = await createPage(t)
  component.form.value.modelName = 'changed-model'
  assert.equal(component.isDirty.value, true)
  setConfigGetFailure()
  const ok = await component.save()
  assert.equal(ok, true)
  // PUT 已被服务端接受（state.config 已合并），回读失败不能让 UI/路由守卫继续认为未保存
  assert.equal(component.isDirty.value, false)
  assert.equal(component.loadError.value, false, '回读失败只提示，不置顶「读取配置失败」横幅')
  assert.equal(state.config.modelName, 'changed-model')
  assert.equal(writes.length, 1)
  assert.ok(messages.some(m => m.kind === 'warning' && m.text.includes('配置已保存，但读取最新配置失败')))
})

test('读取后的精度调整保留服务器基线，失败可重试，保存后才清除草稿', async t => {
  const { component, state, writes, setSaveFailure } = await createPage(t)
  state.config = { ...state.config, temperature: 0.75 }
  await component.reloadConfig()
  assert.equal(component.form.value.temperature, 0.8)
  assert.equal(JSON.parse(component.baseline.value).temperature, 0.75)
  assert.equal(component.isDirty.value, true)
  assert.equal(component.formError.value, '')
  setSaveFailure()
  assert.equal(await component.save(), false)
  assert.equal(component.isDirty.value, true)
  assert.equal(state.config.temperature, 0.75)
  assert.equal(await component.save(), true)
  assert.equal(writes[0].body.temperature, 0.8)
  assert.equal(state.config.temperature, 0.8)
  assert.equal(component.isDirty.value, false)
})

test('畸形响应（缺少可选字段列表）不建立基线，也不清掉已有草稿', async t => {
  const { component, state } = await createPage(t, { initialModel: 'base-model' })
  state.config = {}
  await component.reloadConfig()
  assert.equal(component.loadError.value, true, '空对象必须按读取失败处理')
  assert.equal(component.ready.value, true, '不能清掉已有基线')
  assert.equal(component.form.value.modelName, 'base-model', '不能被空表单覆盖')
})

test('切换搜索服务商清空 Key 与地址并提示重填（P1-4）', async t => {
  const { component, messages } = await createPage(t)
  component.form.value.externalSearch.enabled = true
  component.form.value.externalSearch.provider = 'tavily'
  component.form.value.externalSearch.apiKey = 'search-****'
  component.form.value.externalSearch.baseUrl = 'https://old-search.example.test'
  await component.onSearchProviderChange()
  assert.equal(component.form.value.externalSearch.apiKey, '')
  assert.equal(component.form.value.externalSearch.baseUrl, '')
  assert.match(component.formError.value, /API Key/, '保存前就提示必须重填当前服务商的 Key')
  assert.ok(messages.some(m => m.kind === 'info' && m.text.includes('已切换搜索服务商')))
})

test('选用保存失败时回滚联网策略选择，按钮回到「选用」（P2-7）', async t => {
  const { component, messages, setSaveFailure } = await createPage(t)
  assert.equal(component.form.value.webSearchShape, '')
  setSaveFailure()
  await component.applyProbe({ status: 'verified', shape: 'enable_search', imageStatus: 'not_tested' })
  assert.equal(component.form.value.webSearchShape, '')
  assert.equal(component.form.value.webSearchEndpoint, '')
  assert.equal(component.form.value.webSearchEnabled, false)
  assert.ok(messages.some(m => m.kind === 'error' && m.text.includes('已回退')))
})

test('表单产生未保存修改后清掉预设选中标记（P2-2）', async t => {
  const { component } = await createPage(t)
  component.selectedPresetId.value = 'p1'
  component.form.value.modelName = 'dirty-model'
  await flush()
  assert.equal(component.selectedPresetId.value, null)
})
