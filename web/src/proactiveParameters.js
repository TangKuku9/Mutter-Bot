export const proactiveFields = [
  { key: 'liveliness', label: '发言倾向', type: 'choice', hint: '克制地旁听，或更积极地参与话题。' },
  { key: 'cooldownMinutes', label: '同群发言间隔', type: 'number', min: 1, max: 1440, unit: '分钟', hint: '两次接话之间，至少留出这段时间。' },
  { key: 'maxPerGroupPerDay', label: '每日接话上限', type: 'number', min: 1, max: 200, unit: '次 / 群', hint: '按北京时间自然日累计，同时受全局总量限制。' },
  { key: 'windowSeconds', label: '攒批窗口', type: 'number', min: 15, max: 600, step: 5, unit: '秒', hint: '把这段时间内的消息一起交给 AI 判断。' },
  { key: 'windowMaxMessages', label: '窗口消息上限', type: 'number', min: 2, max: 50, unit: '条', hint: '消息较多时，保留最近的这些条。' },
  { key: 'quietHours', label: '静默时段', type: 'quiet', hint: '北京时间；开始与结束相同表示不设静默。' },
  { key: 'historyMaxMessages', label: '背景历史', type: 'number', min: 0, max: 50, unit: '条', hint: '判定时额外参考的历史消息。' },
  { key: 'maxAttachedImages', label: '附图张数', type: 'number', min: 0, max: 10, unit: '张', hint: '0 表示不附图；仅附带支持格式且仍可读取的图片。' },
  { key: 'evalMaxTokens', label: '判定输出预算', type: 'number', min: 64, max: 8192, step: 64, unit: 'Token', hint: '独立于普通 @ 回复；保存时会按模型上下文容量校准。' },
  { key: 'webSearchEnabled', label: '联网核实', type: 'bool', hint: '仍需 AI 设置中开启联网并有可用通道。' },
  { key: 'memoryInjectionEnabled', label: '参考群记忆', type: 'bool', hint: '仍需群管理允许本群使用记忆。关闭不会删除摘要。' },
  { key: 'allowActiveChannel', label: '允许主动发送通道', type: 'bool', hint: '被动回复槽位过期后，允许改走主动通道。' },
  { key: 'activePerGroupPerMinute', label: '主动通道每分钟上限', type: 'number', min: 1, max: 20, unit: '条 / 群', hint: '仅统计通过主动通道发送的请求。' },
  { key: 'activePerGroupPerDay', label: '主动通道每日上限', type: 'number', min: 1, max: 1000, unit: '条 / 群', hint: '同时受每日接话总量和全局约束限制。' }
]

const keysFor = key => key === 'quietHours' ? ['quietStartHour', 'quietEndHour'] : [key]
// 全局/账号级字段不属于可分群覆盖；保存群参数与预设时始终排除。
const globalOnlyKeys = ['enabled', 'maxGlobalPerDay', 'activePerAccountPerMinute', 'updatedAt',
  'ceilingGroupPerMinute', 'ceilingGroupPerDay', 'ceilingAccountPerMinute', 'ceilingAttachedImages']
export const copy = value => JSON.parse(JSON.stringify(value))
export const parameterValue = (config, key) => key === 'quietHours'
  ? [config.quietStartHour, config.quietEndHour] : config[key]
export const hasOverride = (overrides, key) => keysFor(key).some(part => overrides?.[part] != null)
export const overrideCount = overrides => proactiveFields.filter(field => hasOverride(overrides, field.key)).length

// 排除已知全局字段后保留其余非 null 字段：未来新增的分群参数键会原样透传到后端。
export function onlyParameters(config = {}) {
  return Object.fromEntries(Object.entries(config)
    .filter(([key, value]) => value != null && !globalOnlyKeys.includes(key)))
}

// 与后端返回顺序一致：内置预设在前且保持后端返回顺序，自定义预设按 UpdatedAt 倒序、Id 升序。
export function sortPresets(presets) {
  return [...presets].sort((a, b) => {
    if (a.isBuiltIn && b.isBuiltIn) return 0
    if (!!a.isBuiltIn !== !!b.isBuiltIn) return a.isBuiltIn ? -1 : 1
    const at = Date.parse(a.updatedAt) || 0, bt = Date.parse(b.updatedAt) || 0
    if (at !== bt) return bt - at
    const ai = Number(a.id), bi = Number(b.id)
    if (Number.isInteger(ai) && Number.isInteger(bi)) return ai - bi
    return String(a.id).localeCompare(String(b.id))
  })
}

export function changeParameter(config, key, value, inherit = false) {
  const next = { ...config }
  keysFor(key).forEach((part, index) => {
    if (inherit) delete next[part]
    else next[part] = key === 'quietHours' ? value[index] : value
  })
  return next
}

export function applyParameterPreset(config, preset) {
  return { ...config, ...onlyParameters(preset) }
}

export function effectiveParameters(globalConfig, overrides) {
  return applyParameterPreset(globalConfig, overrides)
}

// 草稿签名与键的插入顺序无关（已知键与透传的未知键都按名字排序后比较）。
export const draftSignature = draft => {
  const params = onlyParameters(draft.overrides)
  return JSON.stringify([!!draft.enabled, Object.keys(params).sort().map(key => [key, params[key]])])
}
export function invalidParameterField(config, includeGlobal = false, fields = proactiveFields) {
  const invalid = fields.find(field => field.type === 'number' && (!Number.isInteger(config[field.key]) || config[field.key] < field.min || config[field.key] > field.max))
  if (invalid) return invalid.label
  if (![config.quietStartHour, config.quietEndHour].every(hour => Number.isInteger(hour) && hour >= 0 && hour <= 23)) return '静默时段'
  if (includeGlobal && (!Number.isInteger(config.maxGlobalPerDay) || config.maxGlobalPerDay < 1 || config.maxGlobalPerDay > 500
    || !Number.isInteger(config.activePerAccountPerMinute) || config.activePerAccountPerMinute < 1 || config.activePerAccountPerMinute > 60)) return '全局总量限制'
  return ''
}
export const hourText = value => `${String(value ?? 0).padStart(2, '0')}:00`
export function parameterText(field, value) {
  if (field.type === 'quiet') return value[0] === value[1] ? '不设静默' : `${hourText(value[0])}–${hourText(value[1])}`
  if (field.type === 'choice') return ['克制', '正常', '主动'][value] || '正常'
  if (field.type === 'bool') return value ? '开启' : '关闭'
  return `${value ?? '—'} ${field.unit || ''}`.trim()
}

export const defaultProactiveConfig = () => ({
  enabled: false, windowSeconds: 60, windowMaxMessages: 12, cooldownMinutes: 45,
  maxPerGroupPerDay: 3, maxGlobalPerDay: 12, quietStartHour: 23, quietEndHour: 8,
  liveliness: 1, allowActiveChannel: true, maxAttachedImages: 3, historyMaxMessages: 8,
  evalMaxTokens: 5120, webSearchEnabled: false, memoryInjectionEnabled: true,
  activePerGroupPerMinute: 3, activePerGroupPerDay: 20, activePerAccountPerMinute: 20, updatedAt: null
})
