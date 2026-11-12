import test from 'node:test'
import assert from 'node:assert/strict'
import { proactiveFields, changeParameter, onlyParameters, sortPresets, applyParameterPreset, effectiveParameters, draftSignature, overrideCount, hasOverride, parameterValue, defaultProactiveConfig, invalidParameterField } from '../src/proactiveParameters.js'

test('群级覆盖和预设只保留可分群参数，显式 false 与 0 不能变成继承', () => {
  assert.deepEqual(onlyParameters({ enabled: true, maxGlobalPerDay: 999, activePerAccountPerMinute: 60, maxAttachedImages: 0, webSearchEnabled: false, cooldownMinutes: null }), { maxAttachedImages: 0, webSearchEnabled: false })
  assert.equal(hasOverride({ webSearchEnabled: false }, 'webSearchEnabled'), true)
  assert.equal(overrideCount({ quietStartHour: 0, quietEndHour: 0, maxAttachedImages: 0 }), 2)
})
test('未知识别键透传到保存载荷，全局字段与 null 继续被排除', () => {
  const payload = onlyParameters({
    enabled: true, maxGlobalPerDay: 999, activePerAccountPerMinute: 60, updatedAt: '2026-01-01',
    ceilingGroupPerMinute: 20, ceilingAttachedImages: 10,
    cooldownMinutes: 10, futureFlag: true, futureText: '新参数', nullKey: null
  })
  assert.deepEqual(payload, { cooldownMinutes: 10, futureFlag: true, futureText: '新参数' })
  // 透传的未知键参与草稿签名与预设应用，避免旧前端把新字段抹掉
  assert.notEqual(draftSignature({ enabled: true, overrides: payload }), draftSignature({ enabled: true, overrides: { cooldownMinutes: 10 } }))
  const global = defaultProactiveConfig()
  const applied = applyParameterPreset(global, { futureFlag: false, liveliness: 2 })
  assert.equal(applied.futureFlag, false)
  assert.equal(applied.liveliness, 2)
})
test('预设排序与后端一致：内置保持返回顺序在前，自定义按 UpdatedAt 倒序、Id 升序', () => {
  const sorted = sortPresets([
    { id: '9', updatedAt: '2026-01-02T00:00:00Z' },
    { id: 'daily', isBuiltIn: true },
    { id: '3', updatedAt: '2026-01-03T00:00:00Z' },
    { id: 'quiet', isBuiltIn: true },
    { id: '2', updatedAt: '2026-01-03T00:00:00Z' },
    { id: '4' }
  ])
  assert.deepEqual(sorted.map(p => p.id), ['daily', 'quiet', '2', '3', '9', '4'])
})
test('清空数值即恢复继承：null 覆盖项不参与来源判断与保存载荷', () => {
  const overrides = { cooldownMinutes: 20, maxPerGroupPerDay: 8 }
  const cleared = changeParameter(overrides, 'cooldownMinutes', null)
  assert.equal(cleared.maxPerGroupPerDay, 8)
  assert.equal(hasOverride(cleared, 'cooldownMinutes'), false)
  assert.deepEqual(onlyParameters(cleared), { maxPerGroupPerDay: 8 })
  assert.equal(effectiveParameters({ cooldownMinutes: 55 }, cleared).cooldownMinutes, 55)
})
test('恢复继承删除覆盖值；全局后续修改自动影响继承项', () => {
  const overrides = { cooldownMinutes: 20, maxPerGroupPerDay: 8 }
  const reset = changeParameter(overrides, 'cooldownMinutes', 45, true)
  assert.deepEqual(reset, { maxPerGroupPerDay: 8 })
  assert.equal(effectiveParameters({ cooldownMinutes: 55 }, reset).cooldownMinutes, 55)
  assert.equal(overrides.cooldownMinutes, 20)
})
test('静默区间作为一项覆盖原子保存和恢复', () => {
  const overrides = changeParameter({}, 'quietHours', [23, 8])
  assert.deepEqual(parameterValue(overrides, 'quietHours'), [23, 8])
  assert.equal(overrideCount(overrides), 1)
  assert.deepEqual(changeParameter(overrides, 'quietHours', [0, 0], true), {})
})
test('应用预设生成独立的参数草稿，保留预设未包含的项和全局限制', () => {
  const global = defaultProactiveConfig()
  const preset = { liveliness: 2, maxAttachedImages: 0, enabled: true, maxGlobalPerDay: 500 }
  const applied = applyParameterPreset(global, preset)
  assert.equal(applied.liveliness, 2)
  assert.equal(applied.enabled, false)
  assert.equal(applied.maxGlobalPerDay, 12)
  assert.equal(applied.cooldownMinutes, 45)
  preset.liveliness = 0
  assert.equal(applied.liveliness, 2)
  assert.equal(global.liveliness, 1)
})
test('群草稿签名忽略属性插入顺序，实际覆盖关系仍参与比较', () => {
  const a = { enabled: true, overrides: { liveliness: 1, cooldownMinutes: 20 } }
  const b = { enabled: true, overrides: { cooldownMinutes: 20, liveliness: 1 } }
  assert.equal(draftSignature(a), draftSignature(b))
  assert.notEqual(draftSignature(a), draftSignature({ enabled: true, overrides: { cooldownMinutes: 20 } }))
  assert.equal(proactiveFields.length, 14)
})
test('清空数值时所有保存入口都能拒绝无效参数', () => {
  const config = defaultProactiveConfig()
  assert.equal(invalidParameterField(config, true), '')
  assert.equal(invalidParameterField({ ...config, cooldownMinutes: undefined }, true), '同群发言间隔')
  assert.equal(invalidParameterField({ ...config, activePerAccountPerMinute: 999 }, true), '全局总量限制')
  assert.equal(invalidParameterField({ ...config, quietEndHour: 24 }), '静默时段')
})
