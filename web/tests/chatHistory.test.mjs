import test from 'node:test'
import assert from 'node:assert/strict'
import { highlightParts, timelineEntries, chatTime, chatDay, decisionTitle, detailTitle, groupSummary, groupRecordingNote } from '../src/chatHistory.js'
test('查找高亮按字面处理正则符号、百分号及 HTML，保持原文不变', () => {
  for (const keyword of ['[x]', '100%_', '<script>', 'a+b']) {
    const text = `之前 ${keyword} 之后 ${keyword}`, parts = highlightParts(text, keyword)
    assert.equal(parts.map(p => p.text).join(''), text)
    assert.equal(parts.filter(p => p.match).length, 2)
  }
  assert.equal(highlightParts('Hello hello', 'HELLO').filter(p => p.match).length, 2)
})
test('倒序分页按正序阅读，并区分相同 ID 的消息与决策事件', () => {
  const rows = [{ kind: 'decision', id: 1, createdAt: '2026-11-10T00:00:01Z' }, { kind: 'message', id: 1, createdAt: '2026-11-09T23:59:59Z' }]
  assert.deepEqual(timelineEntries(rows).filter(e => e.kind !== 'day').map(e => e.key), ['message:1', 'decision:1'])
  assert.equal(rows[0].kind, 'decision')
})
test('历史 UTC 与带偏移时间按北京时间呈现，跨日分组不依赖浏览器时区', () => {
  assert.equal(chatTime('2026-11-09T16:30:00'), '00:30')
  assert.equal(chatDay('2026-11-09T16:30:00Z'), chatDay('2026-11-10T00:30:00+08:00'))
  assert.equal(chatTime('bad-date'), '时间未记录')
})
test('决策留痕标题区分已发送、未发言与发送异常', () => {
  assert.equal(decisionTitle('sent_passive'), '接话详情')
  assert.equal(decisionTitle('sent_active'), '接话详情')
  assert.equal(decisionTitle('skipped_cooldown'), '未发言详情')
  assert.equal(decisionTitle('skipped_disabled'), '未发言详情')
  assert.equal(decisionTitle('failed_ai_call'), '未发言详情')
  assert.equal(decisionTitle('delivery_unknown'), '发送未确认详情')
  assert.equal(decisionTitle('failed_rejected'), '发送失败详情')
  assert.equal(decisionTitle('failed_api_error'), '发送失败详情')
  assert.equal(decisionTitle(undefined), '决策详情')
  assert.equal(decisionTitle(null), '决策详情')
  assert.equal(decisionTitle(''), '决策详情')
})
test('详情标题优先用已加载详情，并区分消息是否关联决策', () => {
  assert.equal(detailTitle({ kind: 'decision', outcome: 'skipped_ai_declined' }, null), '未发言详情')
  assert.equal(detailTitle({ kind: 'decision', outcome: 'skipped_ai_declined' }, { outcome: 'sent_passive' }), '接话详情')
  assert.equal(detailTitle({ kind: 'message', decisionId: 3 }, null), '接话详情')
  assert.equal(detailTitle({ kind: 'message', decisionId: null }, null), '历史详情')
  assert.equal(detailTitle(null, null), '决策详情')
})
test('群列表摘要停用优先，其次提示群级择机', () => {
  assert.equal(groupSummary(null), '按群设置响应')
  assert.equal(groupSummary({ isEnabled: false, aiProactiveEnabled: true }), '群聊已停用')
  assert.equal(groupSummary({ isEnabled: true, aiProactiveEnabled: true }), '群级择机已开启')
  assert.equal(groupSummary({ isEnabled: true, aiProactiveEnabled: false }), '按群设置响应')
  assert.equal(groupSummary({}), '按群设置响应')
})
test('群记录说明区分观察范围与人设，停用群优先', () => {
  assert.equal(groupRecordingNote(null), '按已保存的历史记录展示')
  assert.equal(groupRecordingNote({ isEnabled: false, triggerMode: 2, activeCharacter: true }), '仅查看已保存记录')
  assert.equal(groupRecordingNote({ triggerMode: 2, activeCharacter: true }), '记录全部消息 · 使用群专属人设')
  assert.equal(groupRecordingNote({ triggerMode: 0, activeCharacter: false }), '仅记录被 @ 的消息 · 使用全局人设')
  assert.equal(groupRecordingNote({ triggerMode: 2 }), '记录全部消息 · 使用全局人设')
  assert.equal(groupRecordingNote({ activeCharacter: true }), '仅记录被 @ 的消息 · 使用群专属人设')
})
