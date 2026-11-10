import test from 'node:test'
import assert from 'node:assert/strict'
import { highlightParts, timelineEntries, chatTime, chatDay } from '../src/chatHistory.js'
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
