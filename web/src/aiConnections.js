const STORAGE_KEY = 'mutter.ai-model-preferences.v1'

export function mergeConnectionModels(existing = [], discovered = []) {
  const models = new Map(existing.map(model => [model.id, { ...model }]))
  for (const model of discovered) if (!models.has(model.id)) models.set(model.id, { ...model, isManual: false })
  return [...models.values()]
}

export function readModelPreferences(storage = globalThis.localStorage) {
  try {
    const value = JSON.parse(storage?.getItem(STORAGE_KEY) || '{}')
    return {
      favorites: Array.isArray(value?.favorites) ? value.favorites.filter(validReference).slice(0, 1000) : [],
      recent: Array.isArray(value?.recent) ? value.recent.filter(validReference).slice(0, 20) : [],
      last: value?.last && typeof value.last === 'object' && !Array.isArray(value.last) ? value.last : {}
    }
  } catch { return { favorites: [], recent: [], last: {} } }
}

const validReference = value => value && typeof value.connectionId === 'string' && typeof value.modelId === 'string'
const sameReference = (a, b) => a.connectionId === b.connectionId && a.modelId === b.modelId

export function saveModelPreferences(value, storage = globalThis.localStorage) {
  try { storage?.setItem(STORAGE_KEY, JSON.stringify(value)) } catch { /* 浏览器禁用存储时仍可选择。 */ }
}

export function rememberModel(preferences, connectionId, modelId) {
  const ref = { connectionId, modelId }
  return { ...preferences, last: { ...preferences.last, [connectionId]: modelId },
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
