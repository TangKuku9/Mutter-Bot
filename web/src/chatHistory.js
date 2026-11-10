export function highlightParts(value, keyword = '') {
  const text = String(value ?? '')
  if (!keyword) return [{ text, match: false }]
  const escaped = keyword.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const parts = []
  let cursor = 0
  for (const match of text.matchAll(new RegExp(escaped, 'giu'))) {
    if (match.index > cursor) parts.push({ text: text.slice(cursor, match.index), match: false })
    parts.push({ text: match[0], match: true }); cursor = match.index + match[0].length
  }
  if (cursor < text.length) parts.push({ text: text.slice(cursor), match: false })
  return parts.length ? parts : [{ text, match: false }]
}
function date(value) {
  if (!value) return null
  const parsed = new Date(/[zZ]$|[+-]\d\d:?\d\d$/.test(value) ? value : value + 'Z')
  return Number.isNaN(parsed.getTime()) ? null : parsed
}
const dayFormat = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit' })
const timeFormat = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', hour: '2-digit', minute: '2-digit', hour12: false })
const dateTimeFormat = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
const format = (value, formatter) => { const parsed = date(value); return parsed ? formatter.format(parsed) : '时间未记录' }
export const chatDay = value => format(value, dayFormat)
export const chatTime = value => format(value, timeFormat)
export const chatDateTime = value => format(value, dateTimeFormat)
export const chatKind = row => row.isBotMessage ? (row.isProactive ? '择机接话' : 'Bot 应答') : row.chatType === 1 ? '私聊' : row.hasAtBot ? '被 @' : '围观'
export const senderName = row => row.isBotMessage && (!row.userName || row.userName === 'Bot') ? 'Mutter' : row.userName || row.userId || '未记录发送者'
export const decisionSent = outcome => ['sent_passive', 'sent_active'].includes(outcome)
const sendFailed = outcome => ['failed_rejected', 'failed_rate_limited', 'failed_no_channel', 'failed_api_error'].includes(outcome)
export function decisionTitle(outcome) {
  if (!outcome) return '决策详情'
  if (decisionSent(outcome)) return '接话详情'
  if (outcome === 'delivery_unknown') return '发送未确认详情'
  if (sendFailed(outcome)) return '发送失败详情'
  return '未发言详情'
}
export function detailTitle(entry, detail) {
  const outcome = detail?.outcome ?? (entry?.kind === 'decision' ? entry.outcome : null)
  if (outcome) return decisionTitle(outcome)
  if (entry?.kind === 'message') return entry.decisionId ? '接话详情' : '历史详情'
  return '决策详情'
}
export function groupSummary(config) {
  if (!config) return '按群设置响应'
  if (config.isEnabled === false) return '群聊已停用'
  return config.aiProactiveEnabled ? '群级择机已开启' : '按群设置响应'
}
export function groupRecordingNote(config) {
  if (!config) return '按已保存的历史记录展示'
  if (config.isEnabled === false) return '仅查看已保存记录'
  return `${config.triggerMode === 2 ? '记录全部消息' : '仅记录被 @ 的消息'} · ${config.activeCharacter ? '使用群专属人设' : '使用全局人设'}`
}
export function timelineEntries(items) {
  const entries = []
  let previousDay = ''
  for (const item of [...items].reverse()) {
    const day = chatDay(item.createdAt)
    if (day !== previousDay) entries.push({ kind: 'day', key: `day:${day}`, label: day })
    entries.push({ ...item, key: `${item.kind}:${item.id}` }); previousDay = day
  }
  return entries
}

const timelineTime = item => date(item.createdAt)?.getTime() ?? 0
const timelineKind = item => item.kind === 'decision' ? 1 : 0
const compareTimeline = (left, right) => {
  const byTime = timelineTime(right) - timelineTime(left)
  if (byTime) return byTime
  const byKind = timelineKind(right) - timelineKind(left)
  return byKind || Number(right.id) - Number(left.id)
}

/** SignalR 消息与时间线项使用同一套字段；时间线保持新到旧，与后端分页一致。 */
export function messageTimelineItem(message, groupName = null) {
  const id = Number(message?.id)
  if (!Number.isSafeInteger(id) || id <= 0 || !message?.createdAt) return null
  return {
    kind: 'message', id, createdAt: message.createdAt, chatType: message.chatType ?? 0,
    groupId: message.groupId ?? null, groupName,
    userId: message.userId || '', userName: message.userName || '', plainText: message.plainText || '',
    hasImage: !!message.hasImage, hasAtBot: !!message.hasAtBot,
    isBotMessage: !!message.isBotMessage, isProactive: !!message.isProactive, decisionId: null
  }
}

/** 将实时消息并入当前最新页；重复事件按 kind:id 去重，始终只保留一页。 */
export function mergeTimelineMessage(items, message, { pageSize = 50, groupName = null } = {}) {
  const item = messageTimelineItem(message, groupName)
  if (!item) return items
  const key = `message:${item.id}`
  const next = items.filter(entry => `${entry.kind}:${entry.id}` !== key)
  next.push(item)
  next.sort(compareTimeline)
  return next.slice(0, pageSize)
}

/** 把新的消息行 ID 推入快照上界，供独立总数查询沿用同一冻结范围。 */
export function advanceMessageSnapshot(snapshot, messageId) {
  if (!snapshot) return null
  const parts = String(snapshot).split(':')
  if (parts.length !== 2 || parts.some(part => !/^\d+$/.test(part))) return null
  const id = Number(messageId)
  if (!Number.isSafeInteger(id) || id < 0) return null
  return `${Math.max(Number(parts[0]), id)}:${parts[1]}`
}
