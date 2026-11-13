import test from 'node:test'
import assert from 'node:assert/strict'
import { mergeConnectionModels, preferredModel, groupConnectionModels, rememberModel, readModelPreferences } from '../src/aiConnections.js'

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
