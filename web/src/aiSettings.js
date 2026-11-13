import { tokenBudget, DEFAULT_INPUT_BUDGET } from './tokenBudget.js'

export const defaultExternalSearch = () => ({ enabled: false, provider: 'searxng', baseUrl: '', apiKey: '', maxResults: 5, timeoutSeconds: 8, fallbackToBuiltin: false })

const orEmpty = (value) => (typeof value === 'string' ? value : '')

// 外搜整卡独立归一化：null/非对象回退默认，字符串字段 null → ''，
// 避免「测试搜索」在 .trim() 处抛 TypeError。
function normalizeExternalSearch(value) {
  const source = value && typeof value === 'object' ? value : {}
  return {
    ...defaultExternalSearch(),
    ...source,
    provider: source.provider == null ? 'searxng' : source.provider,
    baseUrl: orEmpty(source.baseUrl),
    apiKey: orEmpty(source.apiKey)
  }
}

export function normalizeAiSettings(data) {
  const source = data && typeof data === 'object' ? data : {}
  return {
    providerConnectionId: '', connections: [], apiBaseUrl: '', apiKey: '', modelName: '', personality: '', systemPrompt: '', temperature: 0.7,
    maxTokens: 2048, maxContextMessages: 20, contextWindowTokens: 32768, maxInputTokens: DEFAULT_INPUT_BUDGET,
    maxAttachedImages: 3, groupMemoryInjectionEnabled: true, webSearchEnabled: false, webSearchImageMode: 'auto',
    availableProviders: [], availableCharacters: [], ...source,
    providerConnectionId: orEmpty(source.providerConnectionId),
    providerConnectionVersion: orEmpty(source.providerConnectionVersion),
    connections: Array.isArray(source.connections) ? source.connections : [],
    // 已选用策略的字符串字段 null/undefined → ''（与后端 string.Empty 默认一致）
    webSearchEndpoint: orEmpty(source.webSearchEndpoint),
    webSearchShape: orEmpty(source.webSearchShape),
    webSearchImageMode: source.webSearchImageMode == null ? 'auto' : source.webSearchImageMode,
    externalSearch: normalizeExternalSearch(source.externalSearch),
    externalSearchStatus: source.externalSearchStatus || { verifiedAt: null }
  }
}

export function aiSettingsError(form) {
  const externalSearch = form.externalSearch && typeof form.externalSearch === 'object' ? form.externalSearch : defaultExternalSearch()
  const temperature = form.temperature
  if (temperature == null || !Number.isFinite(temperature) || temperature < 0 || temperature > 2) return '请填写 0～2 之间的回复随机度。'
  for (const [key, label, min, max] of [
    ['maxTokens', '回复输出上限', 64, 32768], ['contextWindowTokens', '模型上下文容量', 4096, 1048576],
    ['maxInputTokens', '输入预算', 512, 1048576], ['maxContextMessages', '历史消息条数', 2, 100],
    ['maxAttachedImages', '附图张数', 0, form.ceilingAttachedImages || 10]
  ]) if (!Number.isInteger(form[key]) || form[key] < min || form[key] > max) return `请填写 ${min}～${max} 之间的${label}。`
  const budgetError = tokenBudget(form).error
  if (budgetError) return budgetError
  // 外搜通道关闭时这些字段不渲染：旧数据里的非法值不能阻塞整个 AI 配置的保存
  if (externalSearch.enabled) {
    for (const [key, label, min, max] of [['maxResults', '检索结果条数', 1, 10], ['timeoutSeconds', '检索超时', 1, 30]]) {
      const value = externalSearch[key]
      if (!Number.isInteger(value) || value < min || value > max) return `请填写 ${min}～${max} 之间的${label}。`
    }
  }
  return ''
}

// 外搜结果 URL 直接绑定 href 前的白名单：只放行 http/https，
// javascript:/data: 等伪协议一律返回空，由调用方降级为纯文本展示。
export function safeHttpUrl(value) {
  if (typeof value !== 'string' || value.length === 0) return ''
  try {
    const parsed = new URL(value)
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : ''
  } catch {
    return ''
  }
}

const clampInt = (min, max) => (value) => (Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : value)

/**
 * 按界面输入框的 min/max 收敛表单值（服务端保存时会再收敛一次）。
 * 必须在建立 baseline 之前调用：el-input-number 的 precision/step 只改显示不改 model，
 * 否则会出现「显示 0.8、保存 0.75」的二次归一化差，以及越界旧值加载即假脏。
 * 收敛顺序与后端 NormalizeAndValidateAiConfigInput 一致：
 * 先容量 → 再输出（不超过容量-1536）→ 最后输入预算（512..容量-输出-1024）。
 * 返回是否发生了收敛，供页面提示「已按可用值显示」。
 */
export function sanitizeAiSettingsForUi(form) {
  let changed = false
  const fix = (obj, key, sanitize) => {
    const after = sanitize(obj[key])
    if (after !== obj[key]) { obj[key] = after; changed = true }
  }
  const int = (min, max) => clampInt(min, max)

  if (!form.externalSearch || typeof form.externalSearch !== 'object') {
    form.externalSearch = defaultExternalSearch()
    changed = true
  }
  const es = form.externalSearch

  // 字符串字段 null → ''（与后端默认值语义一致）
  for (const key of ['apiBaseUrl', 'apiKey', 'modelName', 'personality', 'systemPrompt']) fix(form, key, orEmpty)
  if (form.webSearchEndpoint == null) { form.webSearchEndpoint = ''; changed = true }
  if (form.webSearchShape == null) { form.webSearchShape = ''; changed = true }
  if (form.webSearchImageMode == null) { form.webSearchImageMode = 'auto'; changed = true }
  fix(es, 'baseUrl', orEmpty)
  fix(es, 'apiKey', orEmpty)

  fix(form, 'contextWindowTokens', int(4096, 1048576))
  fix(form, 'maxTokens', int(64, 32768))
  if (Number.isFinite(form.contextWindowTokens) && Number.isFinite(form.maxTokens)) {
    const cappedOutput = Math.min(form.maxTokens, form.contextWindowTokens - 1536)
    if (cappedOutput !== form.maxTokens) { form.maxTokens = cappedOutput; changed = true }
  }
  const inputMax = Number.isFinite(form.contextWindowTokens) && Number.isFinite(form.maxTokens)
    ? Math.max(512, form.contextWindowTokens - form.maxTokens - 1024)
    : 1048576
  fix(form, 'maxInputTokens', int(512, inputMax))
  fix(form, 'maxContextMessages', int(2, 100))
  fix(form, 'maxAttachedImages', int(0, form.ceilingAttachedImages || 10))
  fix(form, 'temperature', (value) => (Number.isFinite(value) ? Math.min(2, Math.max(0, Math.round(value * 10) / 10)) : value))

  // 外搜数值：非整数回退默认（5/8），越界整数收敛到 1..10 / 1..30；
  // 关闭状态也收敛，避免旧值在切换通道后阻塞保存
  fix(es, 'maxResults', (value) => (Number.isInteger(value) ? Math.min(10, Math.max(1, value)) : 5))
  fix(es, 'timeoutSeconds', (value) => (Number.isInteger(value) ? Math.min(30, Math.max(1, value)) : 8))

  return changed
}
