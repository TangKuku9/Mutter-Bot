import { tokenBudget, DEFAULT_INPUT_BUDGET } from './tokenBudget.js'

export const defaultExternalSearch = () => ({ enabled: false, provider: 'searxng', baseUrl: '', apiKey: '', maxResults: 5, timeoutSeconds: 8, fallbackToBuiltin: false })
export function normalizeAiSettings(data = {}) {
  return {
    apiBaseUrl: '', apiKey: '', modelName: '', personality: '', systemPrompt: '', temperature: 0.7,
    maxTokens: 2048, maxContextMessages: 20, contextWindowTokens: 32768, maxInputTokens: DEFAULT_INPUT_BUDGET,
    maxAttachedImages: 3, groupMemoryInjectionEnabled: true, webSearchEnabled: false, webSearchImageMode: 'auto',
    availableProviders: [], availableCharacters: [], ...data,
    externalSearch: { ...defaultExternalSearch(), ...(data.externalSearch || {}) },
    externalSearchStatus: data.externalSearchStatus || { verifiedAt: null }
  }
}

export function aiSettingsError(form) {
  const temperature = form.temperature
  if (temperature == null || !Number.isFinite(temperature) || temperature < 0 || temperature > 2) return '请填写 0～2 之间的回复随机度。'
  for (const [key, label, min, max] of [
    ['maxTokens', '回复输出上限', 64, 32768], ['contextWindowTokens', '模型上下文容量', 4096, 1048576],
    ['maxInputTokens', '输入预算', 512, 1048576], ['maxContextMessages', '历史消息条数', 2, 100],
    ['maxAttachedImages', '附图张数', 0, form.ceilingAttachedImages || 10]
  ]) if (!Number.isInteger(form[key]) || form[key] < min || form[key] > max) return `请填写 ${min}～${max} 之间的${label}。`
  const budgetError = tokenBudget(form).error
  if (budgetError) return budgetError
  for (const [key, label, min, max] of [['maxResults', '检索结果条数', 1, 10], ['timeoutSeconds', '检索超时', 1, 30]]) {
    const value = form.externalSearch[key]
    if (!Number.isInteger(value) || value < min || value > max) return `请填写 ${min}～${max} 之间的${label}。`
  }
  return ''
}
