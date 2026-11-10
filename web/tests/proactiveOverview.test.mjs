import test from 'node:test'
import assert from 'node:assert/strict'
import { isQuietNow, proactiveRuntime, quietHoursText, todayDecisionCount } from '../src/proactiveOverview.js'

test('静默状态按北京时间判断，跨日、起止边界和同值关闭与后端一致', () => {
  const config = { quietStartHour: 23, quietEndHour: 8 }
  assert.equal(isQuietNow(config, Date.parse('2026-11-09T14:59:59Z')), false)
  assert.equal(isQuietNow(config, Date.parse('2026-11-09T15:00:00Z')), true)
  assert.equal(isQuietNow(config, Date.parse('2026-11-09T23:59:59Z')), true)
  assert.equal(isQuietNow(config, Date.parse('2026-11-10T00:00:00Z')), false)
  assert.equal(isQuietNow({ quietStartHour: 8, quietEndHour: 9 }, Date.parse('2026-11-10T00:00:00Z')), true)
  assert.equal(isQuietNow({ quietStartHour: 8, quietEndHour: 8 }), false)
  assert.equal(quietHoursText(config), '23:00–08:00')
})

test('关闭和零接收量不报异常，未获取状态也不伪装成已关闭', () => {
  assert.equal(proactiveRuntime({}, null).label, '状态未获取')
  const empty = { enabled: false, fullModeArrived: 0, atModeArrived: 10 }
  assert.equal(proactiveRuntime(empty, null).label, '已关闭')
  assert.equal(proactiveRuntime({ ...empty, enabled: true, atModeArrived: 0 }, null).label, '等待消息')
  assert.equal(proactiveRuntime({ ...empty, enabled: true }, null).inspect, true)
})

test('接收异常、静默和正常状态有不同提示，状态取服务端开关', () => {
  const config = { enabled: false, quietStartHour: 23, quietEndHour: 8 }
  const status = { enabled: true, fullModeArrived: 12, atModeArrived: 5 }
  assert.equal(proactiveRuntime(status, config, Date.parse('2026-11-09T15:00:00Z')).label, '静默中')
  assert.equal(proactiveRuntime(status, config, Date.parse('2026-11-10T00:00:00Z')).label, '运行中')
  assert.equal(proactiveRuntime({ ...status, fullModeArrived: 0 }, config).label, '接收需检查')
})

test('今日决策优先取真正进入模型评估的计数，并兼容旧响应', () => {
  assert.equal(todayDecisionCount({ todayDecisions: 3, todayEvaluations: 9 }), 3)
  assert.equal(todayDecisionCount({ todayDecisions: 0, todayEvaluations: 9 }), 0)
  assert.equal(todayDecisionCount({ todayEvaluations: 9 }), 9)
  assert.equal(todayDecisionCount({ todayDecisions: null, todayEvaluations: 9 }), 9)
  assert.equal(todayDecisionCount({ todayEvaluations: 0 }), 0)
  assert.equal(todayDecisionCount({ todayDecisions: '3', todayEvaluations: 9 }), null)
  assert.equal(todayDecisionCount({ todayEvaluations: '9' }), null)
  assert.equal(todayDecisionCount({}), null)
  assert.equal(todayDecisionCount(null), null)
  assert.equal(todayDecisionCount(undefined), null)
})
