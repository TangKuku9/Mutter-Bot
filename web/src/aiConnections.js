const STORAGE_KEY = 'mutter.ai-model-preferences.v1'

// 与服务端 AiConnectionsController.PrepareAsync 的上限保持一致
export const MAX_CONNECTION_MODELS = 2000
export const SECRET_MASK = '********'

// 换了 origin 后打码值必然解析不到旧密钥：在草稿里直接清掉，避免用户提交后才被 400 拦下。
// 返回是否发生了清理，供调用方决定是否提示“请重新填写”。
export function clearMaskedSecretsForOriginChange(draft, headers = []) {
  let cleared = false
  if (draft && draft.apiKey === SECRET_MASK) {
    draft.apiKey = ''
    cleared = true
  }
  for (const header of Array.isArray(headers) ? headers : []) {
    if (header && header.value === SECRET_MASK) {
      header.value = ''
      cleared = true
    }
  }
  return cleared
}

export function groupProviderTemplates(templates = []) {
  return [
    { id: 'china', label: '中国服务商' },
    { id: 'international', label: '国际服务商' },
    { id: 'custom', label: '自定义' }
  ].map(group => ({ ...group, templates: templates.filter(template =>
    (template.category || (template.id === 'custom' ? 'custom' : 'china')) === group.id)
  })).filter(group => group.templates.length)
}

export function mergeConnectionModels(existing = [], discovered = []) {
  const models = new Map(existing.map(model => [model.id, { ...model }]))
  for (const model of discovered) {
    // 发现结果缺省为非手动；内置目录会显式传 isManual:false 与 isBuiltin:true
    if (!models.has(model.id)) models.set(model.id, { ...model, isManual: model.isManual ?? false })
  }
  return [...models.values()]
}

// 合并后超过上限时整体放弃追加，避免“获取模型列表”把已有目录挤掉一半
export function mergeConnectionModelsWithinLimit(existing = [], discovered = [], limit = MAX_CONNECTION_MODELS) {
  const merged = mergeConnectionModels(existing, discovered)
  return merged.length > limit ? { models: existing, exceeded: true } : { models: merged, exceeded: false }
}

export function appendConnectionModel(models = [], model, limit = MAX_CONNECTION_MODELS) {
  if (models.length >= limit) return { models, added: false, reason: 'limit' }
  return { models: [...models, model], added: true, reason: '' }
}

// 请求头关键字过滤：同时匹配模型 ID 与显示名，供连接编辑器与模型选择器复用
export function filterConnectionModels(models = [], query = '') {
  const keyword = String(query ?? '').trim().toLowerCase()
  if (!keyword) return models
  return models.filter(model =>
    String(model?.id ?? '').toLowerCase().includes(keyword)
    || String(model?.name ?? '').toLowerCase().includes(keyword))
}

// 保存连接前的请求头校验。改名后仍提交打码值会让后端按新名称查不到旧值而写入空串，
// 必须在提交前拦下；名称比较不区分大小写（服务端字典同样是 OrdinalIgnoreCase）。
// 新增行没有 originalName，打码值同样解析不到旧值，一并拦下而不是静默存成空串。
export function connectionHeaderError(headers = []) {
  const used = (Array.isArray(headers) ? headers : []).filter(header => header && (header.name || header.value))
  const names = used.map(header => String(header.name ?? '').trim().toLowerCase())
  if (new Set(names).size !== names.length) return '自定义请求头名称不能重复'
  const newMasked = used.find(header => header.value === SECRET_MASK && !header.originalName)
  if (newMasked) return '新增的请求头不能使用打码值，请填写真实值'
  const renamedMasked = used.find(header => header.value === SECRET_MASK
    && header.originalName
    && String(header.name ?? '').trim().toLowerCase() !== String(header.originalName).trim().toLowerCase())
  return renamedMasked ? '改名的请求头需要重新填写值，否则会清空原值' : ''
}

export function readModelPreferences(storage) {
  try {
    // 默认值不能写在参数上：禁用存储的环境里访问 globalThis.localStorage 本身可能抛 SecurityError
    const store = storage ?? globalThis.localStorage
    const value = JSON.parse(store?.getItem(STORAGE_KEY) || '{}')
    return {
      favorites: Array.isArray(value?.favorites) ? value.favorites.filter(validReference).slice(0, 1000) : [],
      recent: Array.isArray(value?.recent) ? value.recent.filter(validReference).slice(0, 20) : [],
      last: value?.last && typeof value.last === 'object' && !Array.isArray(value.last) ? value.last : {}
    }
  } catch { return { favorites: [], recent: [], last: {} } }
}

const validReference = value => value && typeof value.connectionId === 'string' && typeof value.modelId === 'string'
const sameReference = (a, b) => a.connectionId === b.connectionId && a.modelId === b.modelId

export function saveModelPreferences(value, storage) {
  try {
    const store = storage ?? globalThis.localStorage
    store?.setItem(STORAGE_KEY, JSON.stringify(value))
  } catch { /* 浏览器禁用存储时仍可选择。 */ }
}

// 自动同步（切换连接时选中偏好模型）只更新 last，不污染「最近使用」。
export function rememberLastModel(preferences, connectionId, modelId) {
  return { ...preferences, last: { ...preferences.last, [connectionId]: modelId } }
}

// 用户显式选择模型：同时写入 last 与 recent。
export function rememberModel(preferences, connectionId, modelId) {
  const ref = { connectionId, modelId }
  return { ...rememberLastModel(preferences, connectionId, modelId),
    recent: [ref, ...preferences.recent.filter(item => !sameReference(item, ref))].slice(0, 20) }
}

export function preferredModel(connection, preferences) {
  const models = connection?.models || []
  return [preferences.last[connection?.id], connection?.defaultModel, models[0]?.id]
    .find(id => models.some(model => model.id === id)) || ''
}

export function groupConnectionModels(connection, preferences) {
  const models = connection?.models || []
  const byId = new Map(models.map(model => [model.id, model]))
  const used = new Set()
  const collect = references => references.filter(ref => ref.connectionId === connection?.id)
    .map(ref => byId.get(ref.modelId)).filter(model => {
      if (!model || used.has(model.id)) return false
      used.add(model.id)
      return true
    })
  const favorites = collect(preferences.favorites)
  const recent = collect(preferences.recent)
  return [{ label: '收藏', models: favorites }, { label: '最近使用', models: recent },
    { label: '全部模型', models: models.filter(model => !used.has(model.id)) }].filter(group => group.models.length)
}
