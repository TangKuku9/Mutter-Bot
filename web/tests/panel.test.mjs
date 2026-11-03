import test from 'node:test'
import assert from 'node:assert/strict'
import { LatestRequest, matchesChatMessage } from '../src/latestRequest.js'
import { tokenBudget } from '../src/tokenBudget.js'

test('旧请求即使忽略取消、最后返回，也不能覆盖新筛选结果', async () => {
  const request = new LatestRequest()
  let completeOld
  let visible = ''
  const old = request.run(() => new Promise(resolve => { completeOld = resolve }), value => { visible = value })
  await request.run(async () => 'new group', value => { visible = value })
  completeOld('old group')
  await old
  assert.equal(visible, 'new group')
  assert.equal(request.pending, false)
})

test('离开页面后的请求不能再写回；取消不产生业务错误', async () => {
  const request = new LatestRequest()
  let resolve
  let updates = 0
  const pending = request.run(() => new Promise(r => { resolve = r }), () => updates++)
  request.cancel()
  resolve('late result')
  await pending
  assert.equal(updates, 0)
  assert.equal(request.pending, false)
})

test('群筛选、围观、被 @、Bot 回复和插话使用相同过滤条件', () => {
  const observed = { chatType: 0, groupId: 'GROUP-A', isBotMessage: false, hasAtBot: false }
  const privateMessage = { ...observed, chatType: 1, groupId: null }
  assert.equal(matchesChatMessage(observed, { groupId: 'GROUP-B' }), false)
  assert.equal(matchesChatMessage(privateMessage, { source: 'observe' }), false)
  assert.equal(matchesChatMessage(observed, { source: 'observe' }), true)
  assert.equal(matchesChatMessage({ ...observed, hasAtBot: true }, { source: 'at' }), true)
  assert.equal(matchesChatMessage({ ...observed, isBotMessage: true, isProactive: true }, { source: 'bot' }), false)
  assert.equal(matchesChatMessage({ ...observed, isBotMessage: true, isProactive: true }, { source: 'proactive' }), true)
})

test('容量同时预留输出与安全余量，切换小模型时能发现不相容的输入预算', () => {
  assert.equal(tokenBudget({}).error, '')
  const invalid = tokenBudget({ contextWindowTokens: 4096, maxTokens: 2048, maxInputTokens: 2048 })
  assert.equal(invalid.availableInput, 1024)
  assert.notEqual(invalid.error, '')
  assert.equal(tokenBudget({ contextWindowTokens: 4096, maxTokens: 2048, maxInputTokens: 1024 }).error, '')
  assert.notEqual(tokenBudget({ maxInputTokens: 511 }).error, '')
})
