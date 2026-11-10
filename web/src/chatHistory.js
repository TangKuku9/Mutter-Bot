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
const format = (value, options) => date(value)?.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', ...options }) || '时间未记录'
export const chatDay = value => format(value, { year: 'numeric', month: '2-digit', day: '2-digit' })
export const chatTime = value => format(value, { hour: '2-digit', minute: '2-digit', hour12: false })
export const chatDateTime = value => format(value, { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
export const chatKind = row => row.isBotMessage ? (row.isProactive ? '择机接话' : 'Bot 应答') : row.chatType === 1 ? '私聊' : row.hasAtBot ? '被 @' : '围观'
export const senderName = row => row.isBotMessage && (!row.userName || row.userName === 'Bot') ? 'Mutter' : row.userName || row.userId || '未记录发送者'
export const decisionSent = outcome => ['sent_passive', 'sent_active'].includes(outcome)
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
