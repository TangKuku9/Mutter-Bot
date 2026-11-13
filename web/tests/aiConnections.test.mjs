import test from 'node:test'
import assert from 'node:assert/strict'
import {
  MAX_CONNECTION_MODELS, appendConnectionModel, clearMaskedSecretsForOriginChange,
  connectionHeaderError, filterConnectionModels,
  mergeConnectionModels, mergeConnectionModelsWithinLimit, preferredModel, groupConnectionModels,
  rememberModel, rememberLastModel, readModelPreferences
} from '../src/aiConnections.js'

test('换 origin 只清掉打码值，用户新填的密钥与请求头保持原样', () => {
  const draft = { apiKey: '********' }
  const headers = [{ name: 'X-Tenant', value: '********' }, { name: 'X-Trace', value: 'typed-value' }]
  assert.equal(clearMaskedSecretsForOriginChange(draft, headers), true)
  assert.equal(draft.apiKey, '')
  assert.equal(headers[0].value, '')
  assert.equal(headers[1].value, 'typed-value')
  assert.equal(clearMaskedSecretsForOriginChange({ apiKey: 'fresh-key' }, [{ name: 'X', value: 'fresh' }]), false,
    '没有打码值时不应误报清理')
})

test('模型刷新保留手动目录、名称与限额，准确区分大小写 ID', () => {
  const existing = [{ id: 'Model/A', name: '我的模型', isManual: true, contextWindowTokens: 8192 }]
  const result = mergeConnectionModels(existing, [{ id: 'Model/A', name: 'remote' }, { id: 'model/a', name: 'new' }])
  assert.deepEqual(result[0], existing[0])
  assert.equal(result[1].id, 'model/a')
  assert.equal(result[1].isManual, false)
  assert.notEqual(result[0], existing[0])
})

test('收藏与最近记录按连接隔离，同名模型不会串到另一账号', () => {
  const models = [{ id: 'shared' }, { id: 'other' }]
  const prefs = { favorites: [{ connectionId: 'a', modelId: 'shared' }], recent: [], last: {} }
  const recent = rememberModel(rememberModel(prefs, 'a', 'other'), 'b', 'shared')
  assert.equal(groupConnectionModels({ id: 'a', models }, recent)[0].label, '收藏')
  assert.equal(groupConnectionModels({ id: 'b', models }, recent)[0].label, '最近使用')
  assert.equal(preferredModel({ id: 'a', models, defaultModel: 'shared' }, recent), 'other')
  assert.equal(preferredModel({ id: 'b', models, defaultModel: 'other' }, recent), 'shared')
  assert.equal(preferredModel({ id: 'c', models, defaultModel: 'other' }, recent), 'other')
})

test('过期收藏不出现，重复选择不制造重复最近项，坏存储不阻塞选择', () => {
  const prefs = { favorites: [{ connectionId: 'a', modelId: 'gone' }], recent: [], last: { a: 'gone' } }
  const connection = { id: 'a', models: [{ id: 'kept' }], defaultModel: 'kept' }
  assert.equal(preferredModel(connection, prefs), 'kept')
  assert.deepEqual(groupConnectionModels(connection, prefs), [{ label: '全部模型', models: connection.models }])
  const selected = rememberModel(rememberModel(prefs, 'a', 'kept'), 'a', 'kept')
  assert.equal(selected.recent.length, 1)
  assert.deepEqual(readModelPreferences({ getItem() { throw new Error('disabled') } }), { favorites: [], recent: [], last: {} })
})

test('模型目录过滤同时匹配 ID 与显示名，不区分大小写', () => {
  const models = [
    { id: 'deepseek-chat', name: '日常对话' },
    { id: 'other-model', name: 'DeepSeek 推理' },
    { id: 'plain', name: 'PLAIN' }
  ]
  assert.deepEqual(filterConnectionModels(models, 'deepseek').map(model => model.id), ['deepseek-chat', 'other-model'])
  assert.deepEqual(filterConnectionModels(models, '推理').map(model => model.id), ['other-model'])
  assert.deepEqual(filterConnectionModels(models, '  CHAT ').map(model => model.id), ['deepseek-chat'])
  assert.equal(filterConnectionModels(models, ''), models, '空查询保持原数组，避免无谓重建')
  assert.deepEqual(filterConnectionModels(models, 'missing'), [])
})

test('请求头改名后仍提交打码值会被拦截，仅改大小写不受影响', () => {
  assert.equal(connectionHeaderError([{ name: 'X-Key', value: '********', originalName: 'X-Key' }]), '')
  assert.equal(connectionHeaderError([{ name: 'x-key', value: '********', originalName: 'X-Key' }]), '')
  assert.equal(connectionHeaderError([{ name: 'X-Other', value: '********', originalName: 'X-Key' }]),
    '改名的请求头需要重新填写值，否则会清空原值')
  // 用户已经重填了新值则允许改名
  assert.equal(connectionHeaderError([{ name: 'X-Other', value: 'fresh', originalName: 'X-Key' }]), '')
  // 新增行（没有 originalName）没有旧值可解析：打码值必须拦下，否则会被静默存成空串
  assert.equal(connectionHeaderError([{ name: 'X-New', value: '********' }]),
    '新增的请求头不能使用打码值，请填写真实值')
  assert.equal(connectionHeaderError([
    { name: 'X-Key', value: 'a', originalName: 'X-Key' },
    { name: 'x-key', value: 'b', originalName: 'x-key' }
  ]), '自定义请求头名称不能重复')
})

test('模型总数超过 2000 时阻止追加，合并与手动添加都保留原有目录', () => {
  const full = Array.from({ length: MAX_CONNECTION_MODELS }, (_, index) => ({ id: `model-${index}` }))
  const appended = appendConnectionModel(full, { id: 'overflow' })
  assert.equal(appended.added, false)
  assert.equal(appended.reason, 'limit')
  assert.equal(appended.models.length, MAX_CONNECTION_MODELS)
  assert.equal(appended.models.some(model => model.id === 'overflow'), false)

  const stillRoom = appendConnectionModel(full.slice(0, MAX_CONNECTION_MODELS - 1), { id: 'last' })
  assert.equal(stillRoom.added, true)
  assert.equal(stillRoom.models.length, MAX_CONNECTION_MODELS)
  assert.equal(stillRoom.models.at(-1).id, 'last')

  const overflow = mergeConnectionModelsWithinLimit(full, [{ id: 'overflow', name: 'overflow' }])
  assert.equal(overflow.exceeded, true)
  assert.equal(overflow.models, full, '超限时整体放弃追加，已有目录原样保留')
  assert.equal(overflow.models.some(model => model.id === 'overflow'), false)

  const exactlyFull = mergeConnectionModelsWithinLimit(full.slice(0, MAX_CONNECTION_MODELS - 1), [{ id: 'last' }])
  assert.equal(exactlyFull.exceeded, false)
  assert.equal(exactlyFull.models.length, MAX_CONNECTION_MODELS)
  assert.equal(exactlyFull.models.at(-1).id, 'last')
})

test('自动同步只更新 last，显式选择才写入最近使用', () => {
  const base = { favorites: [], recent: [{ connectionId: 'a', modelId: 'old' }], last: {} }
  const auto = rememberLastModel(base, 'a', 'auto')
  assert.deepEqual(auto.last, { a: 'auto' })
  assert.deepEqual(auto.recent, base.recent, '切换连接自动选中的模型不污染最近使用')
  const explicit = rememberModel(base, 'a', 'auto')
  assert.deepEqual(explicit.last, { a: 'auto' })
  assert.deepEqual(explicit.recent.map(item => item.modelId), ['auto', 'old'])
})

test('merge 把缺省来源视为非手动，并保留内置标记', () => {
  const merged = mergeConnectionModels([], [{ id: 'builtin', isBuiltin: true }, { id: 'remote' }])
  assert.equal(merged[0].isManual, false)
  assert.equal(merged[0].isBuiltin, true)
  assert.equal(merged[1].isManual, false)
  assert.equal(merged[1].isBuiltin, undefined)
})
