export function quietHoursText(config) {
  if (!config) return '—'
  const { quietStartHour: start, quietEndHour: end } = config
  if (start === end) return '不静默'
  const hour = value => String(value).padStart(2, '0') + ':00'
  return `${hour(start)}–${hour(end)}`
}

export function isQuietNow(config, now = Date.now()) {
  if (!config || config.quietStartHour === config.quietEndHour) return false
  const hour = new Date(Number(now) + 8 * 60 * 60 * 1000).getUTCHours()
  const { quietStartHour: start, quietEndHour: end } = config
  return start < end ? hour >= start && hour < end : hour >= start || hour < end
}

export function proactiveRuntime(status, config, now = Date.now()) {
  if (typeof status.enabled !== 'boolean') return { type: 'info', label: '状态未获取', text: '正在等待运行数据，可点击刷新重试。' }
  if (!status.enabled) return { type: 'info', label: '已关闭', text: '已关闭，暂不采集消息、调用 AI 或主动发言。' }
  if (status.fullModeArrived === 0 && status.atModeArrived > 0) {
    return { type: 'warning', label: '接收需检查', text: '只收到 @ 消息，尚未收到全量群消息。', inspect: true }
  }
  if (isQuietNow(config, now)) return { type: 'info', label: '静默中', text: '当前处于静默时段，暂不主动发言。' }
  if (status.fullModeArrived === 0) return { type: 'info', label: '等待消息', text: '已启用，等待群消息到达。' }
  return { type: 'success', label: '运行中', text: '已启用，有合适的话题时自然接话。' }
}
